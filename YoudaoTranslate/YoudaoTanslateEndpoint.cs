using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;

namespace YoudaoTranslate
{
   /// <summary>
   /// XUnity.AutoTranslator 有道在线翻译 (Web) 端点 —— 2026 版实现。
   ///
   /// 有道网页端走 dict-trans.youdao.com 的 “webmain” SSE 流式协议：
   ///   1) POST {KeyEndpoint}      （默认 https://dict-trans.youdao.com/translate/key）
   ///      用产品常量密钥按 v3 规则签名，换取动态 secretKey + token（长时效）；
   ///   2) POST {TranslateEndpoint}（默认 https://dict-trans.youdao.com/webtranslate/sse）
   ///      用 secretKey 按 v3 规则签名，SSE 流返回 transIncre 分片，拼装即为译文。
   ///
   /// 本实现自带轻量 HTTP 客户端（HttpWebRequest），不依赖 XUnity 自带的 WebClient，
   /// 以兼容有道返回的 chunked / SSE 响应，并规避其非浏览器客户端的 TLS 风控误伤。
   /// 接口地址、前端常量与密钥均可通过 ini [Youdao] 段覆盖（见 README）。
   /// </summary>
   public class YoudaoTranslateEndpoint : ITranslateEndpoint
   {
      // ---- 接口地址（可被 ini 覆盖）----
      private const string DefaultKeyEndpointUrl = "https://dict-trans.youdao.com/translate/key";
      private const string DefaultTranslateEndpointUrl = "https://dict-trans.youdao.com/webtranslate/sse";

      // ---- YNMT 模式（网页 TextTranslate 默认引擎）使用的旧版接口 ----
      private const string LegacyKeyUrl = "https://dict.youdao.com/webtranslate/key";
      private const string LegacyTranslateUrl = "https://dict.youdao.com/webtranslate";
      private const string LegacyKeyGetterKeyId = "webfanyi-key-getter-2025";
      private const string LegacyConstSign = "yU5nT5dK3eZ1pI4j";

      // ---- 产品常量（提取自 fanyi.youdao.com 前端 app.js，有道更新前端后可能轮换）----
      private const string DefaultKeyGetterKeyId = "translate-webmain-key-getter";
      private const string DefaultKeyGetterConstSign = "kSy5gtKA4yRUxAVPJPrdYKZ0jBKyd3t1";
      private const string DefaultTranslateKeyId = "translate-webfanyi-webmain";

      private const string Product = "webfanyi";
      private const string Keyfrom = "webfanyi.webmain";
      private const string Client = "webmain";
      private const string AppVersion = "12.0.0";
      private const string DefaultModel = "llmLite";
      private const string DefaultYduuid = "abcdefg";

      private const string Referer = "https://fanyi.youdao.com/";
      private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

      // ---- 运行期 ----
      private readonly Stopwatch _clock = Stopwatch.StartNew();

      private string _yduuid = DefaultYduuid;
      private string _cookieHeader = "";
      private double _delaySeconds = 1.0;
      private double _keyRefreshSeconds = 600.0;
      private string _model = DefaultModel;
      private string _keyGetterKeyId = DefaultKeyGetterKeyId;
      private string _keyGetterConstSign = DefaultKeyGetterConstSign;
      private string _translateKeyId = DefaultTranslateKeyId;
      private string _keyEndpointUrl = DefaultKeyEndpointUrl;
      private string _translateEndpointUrl = DefaultTranslateEndpointUrl;

      private string _secretKey;
      private string _token;
      private bool _manualKeys;
      private string _lastKeyError;
      private double _lastKeyFetchTime = double.MinValue;
      private double _lastKeyAttemptTime = double.MinValue;
      private double _lastRequestStartTime = double.MinValue;

      // YNMT 模式密钥缓存
      private string _mode = "lite"; // lite=webmain SSE(llmLite)，ynmt=网页默认 YNMT 旧通道
      private string _ynmtSecret;
      private string _ynmtAesKey;
      private string _ynmtAesIv;
      private double _ynmtLastFetch = double.MinValue;
      private double _ynmtLastAttempt = double.MinValue;

      // 半自动 Cookie（Mode=ynmt 且 ini Cookie 留空时，从 CookieFile 自动读取/失效重读）
      private string _cookieFile = "youdao-Cookie配置.txt";
      private bool _cookiePrompt = true;
      private bool _cookieFromFile;
      private double _lastPromptTime = double.MinValue;

      public string Id => "YoudaoTranslate";

      public string FriendlyName => "Youdao Translate (Web)";

      public int MaxConcurrency => 1;

      public int MaxTranslationsPerRequest => 1;

      public void Initialize( IInitializationContext context )
      {
         string cookie = context.GetOrCreateSetting( "Youdao", "Cookie", "" );
         if( !string.IsNullOrWhiteSpace( cookie ) )
         {
            string value = cookie.Trim();
            if( !value.StartsWith( "OUTFOX_SEARCH_USER_ID=", StringComparison.OrdinalIgnoreCase ) )
            {
               value = "OUTFOX_SEARCH_USER_ID=" + value;
            }
            _cookieHeader = value;
         }

         _delaySeconds = Math.Max( 0, context.GetOrCreateSetting( "Youdao", "DelaySeconds", 1.0f ) );
         _keyRefreshSeconds = Math.Max( 5, context.GetOrCreateSetting( "Youdao", "KeyRefreshSeconds", 600.0f ) );

         string yduuid = context.GetOrCreateSetting( "Youdao", "Yduuid", "" );
         _yduuid = string.IsNullOrWhiteSpace( yduuid ) ? NewYduuid() : yduuid.Trim();

         _model = context.GetOrCreateSetting( "Youdao", "Model", DefaultModel );
         if( string.IsNullOrWhiteSpace( _model ) ) _model = DefaultModel;

         string mode = context.GetOrCreateSetting( "Youdao", "Mode", "lite" );
         _mode = string.Equals( mode, "ynmt", StringComparison.OrdinalIgnoreCase ) ? "ynmt" : "lite";

         // 半自动 Cookie：Cookie 留空时从 CookieFile 读取；失效后自动重读，仍失败则弹窗提示
         string cookieFile = context.GetOrCreateSetting( "Youdao", "CookieFile", "youdao-Cookie配置.txt" );
         if( !string.IsNullOrWhiteSpace( cookieFile ) ) _cookieFile = cookieFile.Trim();
         _cookiePrompt = context.GetOrCreateSetting( "Youdao", "CookiePrompt", true );
         if( string.IsNullOrWhiteSpace( _cookieHeader ) && _mode == "ynmt" )
         {
            LoadCookieFromFile();
         }

         _keyGetterKeyId = context.GetOrCreateSetting( "Youdao", "KeyGetterKeyId", DefaultKeyGetterKeyId );
         _keyGetterConstSign = context.GetOrCreateSetting( "Youdao", "KeyGetterConstSign", DefaultKeyGetterConstSign );
         _translateKeyId = context.GetOrCreateSetting( "Youdao", "TranslateKeyId", DefaultTranslateKeyId );

         string keyEp = context.GetOrCreateSetting( "Youdao", "KeyEndpoint", "" );
         string trEp = context.GetOrCreateSetting( "Youdao", "TranslateEndpoint", "" );
         if( !string.IsNullOrWhiteSpace( keyEp ) ) _keyEndpointUrl = keyEp.Trim();
         if( !string.IsNullOrWhiteSpace( trEp ) ) _translateEndpointUrl = trEp.Trim();

         // 手动注入密钥（可选）：当 key 接口被有道风控时，可先从浏览器取得 secretKey/token 填到这里，
         // 插件将跳过取密钥请求直接翻译（密钥为长时效）。
         string manualSecret = context.GetOrCreateSetting( "Youdao", "SecretKey", "" );
         string manualToken = context.GetOrCreateSetting( "Youdao", "Token", "" );
         if( !string.IsNullOrWhiteSpace( manualSecret ) && !string.IsNullOrWhiteSpace( manualToken ) )
         {
            _secretKey = manualSecret.Trim();
            _token = manualToken.Trim();
            _manualKeys = true;
            _lastKeyFetchTime = _clock.Elapsed.TotalSeconds;
         }
      }

      public IEnumerator Translate( ITranslationContext context )
      {
         // 1) 限速
         double nextAllowed = _lastRequestStartTime + _delaySeconds;
         while( _clock.Elapsed.TotalSeconds < nextAllowed )
         {
            yield return null;
         }
         _lastRequestStartTime = _clock.Elapsed.TotalSeconds;

         // YNMT 模式走旧版通道（与网页 TextTranslate 默认引擎一致）
         if( _mode == "ynmt" )
         {
            IEnumerator yn = YnmtTranslateCoroutine( context );
            while( yn.MoveNext() ) yield return yn.Current;
            yield break;
         }

         // 2) 确保有密钥
         if( ShouldRefreshKeys() )
         {
            IEnumerator fetch = FetchKeysCoroutine();
            while( fetch.MoveNext() ) yield return fetch.Current;
         }

         if( string.IsNullOrEmpty( _secretKey ) || string.IsNullOrEmpty( _token ) )
         {
            string detail = string.IsNullOrEmpty( _lastKeyError ) ? "" : " 详情: " + _lastKeyError;
            context.Fail( "有道取密钥失败（可能被接口风控）。请稍后重试，或按 README 在 [Youdao] 配置 SecretKey/Token 后重试。" + detail );
            yield break;
         }

         // 3) 翻译请求
         string from = FixLanguage( context.SourceLanguage );
         string to = FixLanguage( context.DestinationLanguage );
         string text = context.UntranslatedText.Replace( "「", "\"" ).Replace( "」", "\"" );
         if( string.IsNullOrEmpty( text ) )
         {
            context.Complete( text );
            yield break;
         }

         string encodedI = JsEncodeComponent( text );

         var extra = new Dictionary<string, string>
         {
            { "modelName", _model },
            { "useTerm", "false" },
            { "i", encodedI },
            { "from", from },
            { "to", to },
            { "signSecretKey", _secretKey },
            { "keyId", _translateKeyId },
            { "token", _token },
            { "source", "webmain" },
         };

         var signed = BuildSignedParams( _secretKey, _translateKeyId, extra );
         string body = BuildQueryString( signed );

         string responseText = null;
         string error = null;
         var task = Task.Run( () => HttpPost( _translateEndpointUrl, body, "application/x-www-form-urlencoded; charset=UTF-8" ) );
         while( !task.IsCompleted )
         {
            yield return null;
         }
         if( task.IsFaulted || task.IsCanceled )
         {
            Exception inner = task.Exception != null && task.Exception.InnerException != null ? task.Exception.InnerException : null;
            error = ( inner == null ? "请求异常" : inner.GetType().Name + ": " + inner.Message )
                  + ( inner != null && !string.IsNullOrEmpty( inner.StackTrace ) ? " | " + inner.StackTrace : "" );
         }
         else
         {
            responseText = task.Result;
         }

         if( error != null || string.IsNullOrEmpty( responseText ) )
         {
            context.Fail( "有道翻译请求失败: " + ( error ?? "空响应" ) );
            yield break;
         }

         // 4) 校验 JSON 错误（签名/令牌失效等）
         int code;
         if( IsErrorJson( responseText, out code ) && code != 0 )
         {
            if( !_manualKeys )
            {
               InvalidateKeys();
            }
            DumpDebug( from, to, text, "服务端错误码 " + code, responseText );
            context.Fail( "有道返回错误码 " + code );
            yield break;
         }

         // 5) 解析 SSE 译文
         string translated = ParseSseTranslation( responseText );
         if( string.IsNullOrEmpty( translated ) )
         {
            DumpDebug( from, to, text, "SSE 无 transIncre", responseText );
            context.Fail( "有道未返回可解析的译文" );
            yield break;
         }

         context.Complete( translated );
      }

      /// <summary>失败时把请求/响应片段写入游戏目录 YoudaoTranslate.debug.log，便于排障。</summary>
      private static void DumpDebug( string from, string to, string text, string why, string responseText )
      {
         try
         {
            string snippet = string.IsNullOrEmpty( responseText ) ? "<空>" : ( responseText.Length > 400 ? responseText.Substring( 0, 400 ) : responseText );
            var sb = new StringBuilder();
            sb.AppendLine( "==== " + DateTime.Now.ToString( "yyyy-MM-dd HH:mm:ss" ) + " ====" );
            sb.AppendLine( "from=" + from + " to=" + to );
            sb.AppendLine( "text=" + text );
            sb.AppendLine( "why=" + why );
            sb.AppendLine( "response=" + snippet );
            sb.AppendLine();
            System.IO.File.AppendAllText( "YoudaoTranslate.debug.log", sb.ToString() );
         }
         catch
         {
         }
      }

      // ------------------------------------------------------------------
      // YNMT 模式（网页 TextTranslate 默认“通用场景”引擎）
      // ------------------------------------------------------------------

      private IEnumerator YnmtTranslateCoroutine( ITranslationContext context )
      {
         string from = FixLanguage( context.SourceLanguage );
         string to = FixLanguage( context.DestinationLanguage );
         string text = context.UntranslatedText.Replace( "「", "\"" ).Replace( "」", "\"" );
         if( string.IsNullOrEmpty( text ) )
         {
            context.Complete( text );
            yield break;
         }

         string failReason = null;

         // 半自动重试：最多两轮。第一轮失败（且 Cookie 来自文件）时清掉 Cookie 并重读文件（用户可能已重跑一键脚本刷新），再试一轮。
         for( int attempt = 0; attempt < 2; attempt++ )
         {
            if( string.IsNullOrEmpty( _cookieHeader ) && !LoadCookieFromFile() )
            {
               failReason = "未配置 Cookie（[Youdao] Cookie 或 youdao-Cookie配置.txt）";
               if( _cookieFromFile ) ResetCookieFromFile();
               continue;
            }

            // 1) 确保 YNMT 密钥
            double now = _clock.Elapsed.TotalSeconds;
            bool have = !string.IsNullOrEmpty( _ynmtSecret ) && !string.IsNullOrEmpty( _ynmtAesKey ) && !string.IsNullOrEmpty( _ynmtAesIv );
            if( !have || now - _ynmtLastFetch >= _keyRefreshSeconds )
            {
               if( now - _ynmtLastAttempt >= 10 )
               {
                  IEnumerator fetch = YnmtFetchKeysCoroutine();
                  while( fetch.MoveNext() ) yield return fetch.Current;
               }
            }

            if( string.IsNullOrEmpty( _ynmtSecret ) )
            {
               failReason = "取密钥失败: " + ( string.IsNullOrEmpty( _lastKeyError ) ? "未知原因" : _lastKeyError );
               if( _cookieFromFile && attempt == 0 )
               {
                  ResetCookieFromFile();
                  continue; // 尝试用刷新后的 Cookie 重试一轮
               }
               break;
            }

            // 2) 翻译
            long t2 = CurrentTimeMillis();
            string sign = Md5Hex( "client=fanyideskweb&mysticTime=" + t2 + "&product=webfanyi&key=" + _ynmtSecret );

            var fields = new Dictionary<string, string>( StringComparer.Ordinal )
            {
               { "i", text },
               { "from", from },
               { "to", to },
               { "useTerm", "false" },
               { "dictResult", "false" },
               { "keyid", "webfanyi" },
               { "client", "fanyideskweb" },
               { "product", "webfanyi" },
               { "appVersion", "1.0.0" },
               { "vendor", "web" },
               { "pointParam", "client,mysticTime,product" },
               { "keyfrom", "fanyi.web" },
               { "mysticTime", t2.ToString() },
               { "sign", sign },
            };
            string body = BuildQueryString( fields );

            string responseText = null;
            string error = null;
            var task = Task.Run( () => HttpPost( LegacyTranslateUrl, body, "application/x-www-form-urlencoded; charset=UTF-8" ) );
            while( !task.IsCompleted )
            {
               yield return null;
            }
            if( task.IsFaulted || task.IsCanceled )
            {
               Exception inner = task.Exception != null && task.Exception.InnerException != null ? task.Exception.InnerException : null;
               error = ( inner == null ? "请求异常" : inner.GetType().Name + ": " + inner.Message )
                     + ( inner != null && !string.IsNullOrEmpty( inner.StackTrace ) ? " | " + inner.StackTrace : "" );
            }
            else
            {
               responseText = task.Result;
            }

            if( error != null || string.IsNullOrEmpty( responseText ) )
            {
               failReason = "YNMT 请求失败: " + ( error ?? "空响应" );
               if( _cookieFromFile && attempt == 0 )
               {
                  ResetCookieFromFile();
                  continue;
               }
               break;
            }

            // 3) 明文错误则重试/提示；否则 AES-CBC 解密
            int code;
            if( IsErrorJson( responseText, out code ) && code != 0 )
            {
               _lastKeyError = "服务端错误码 " + code;
               _ynmtLastFetch = double.MinValue;
               failReason = "YNMT 返回错误码 " + code;
               if( _cookieFromFile && attempt == 0 )
               {
                  ResetCookieFromFile();
                  continue;
               }
               break;
            }

            string decrypted;
            try
            {
               decrypted = YnmtDecrypt( responseText );
            }
            catch( Exception ex )
            {
               DumpDebug( from, to, text, "YNMT 解密失败: " + ex.GetType().Name + ": " + ex.Message, responseText );
               failReason = "YNMT 响应解密失败: " + ex.Message;
               break;
            }

            string translated = ExtractYnmtText( decrypted );
            if( string.IsNullOrEmpty( translated ) )
            {
               DumpDebug( from, to, text, "YNMT 无译文", decrypted );
               failReason = "YNMT 未返回可解析的译文";
               break;
            }

            context.Complete( translated );
            yield break;
         }

         // 走到这里说明两轮都失败：弹窗/提示用户刷新 Cookie（文件模式下半自动恢复）
         if( _cookiePrompt && _clock.Elapsed.TotalSeconds - _lastPromptTime > 60 )
         {
            _lastPromptTime = _clock.Elapsed.TotalSeconds;
            PromptRefreshCookie();
         }
         context.Fail( "YNMT 翻译失败: " + failReason );
      }

      /// <summary>尝试从 CookieFile 读取 Cookie 串（文件由《获取Cookie一键.bat》生成）。</summary>
      private bool LoadCookieFromFile()
      {
         try
         {
            string path = FindCookieFile();
            if( path == null ) return false;

            string[] lines = System.IO.File.ReadAllLines( path, Encoding.UTF8 );
            foreach( string rawLine in lines )
            {
               string line = rawLine.Trim();
               if( line.Length == 0 || line.StartsWith( ";" ) || line.StartsWith( "[" ) ) continue;
               int eq = line.IndexOf( '=' );
               if( eq <= 0 ) continue;
               string key = line.Substring( 0, eq ).Trim();
               string value = line.Substring( eq + 1 ).Trim();
               if( key.Equals( "Cookie", StringComparison.OrdinalIgnoreCase ) && value.Length > 0 )
               {
                  _cookieHeader = value;
                  _cookieFromFile = true;
                  return true;
               }
            }
            return false;
         }
         catch
         {
            return false;
         }
      }

      private string FindCookieFile()
      {
         var candidates = new List<string>();
         if( System.IO.Path.IsPathRooted( _cookieFile ) )
         {
            candidates.Add( _cookieFile );
         }
         else
         {
            try { candidates.Add( System.IO.Path.Combine( Environment.CurrentDirectory, _cookieFile ) ); } catch { }
            try { candidates.Add( System.IO.Path.Combine( AppDomain.CurrentDomain.BaseDirectory, _cookieFile ) ); } catch { }
            try
            {
               string asmDir = System.IO.Path.GetDirectoryName( typeof(YoudaoTranslateEndpoint).Assembly.Location );
               if( !string.IsNullOrEmpty( asmDir ) ) candidates.Add( System.IO.Path.Combine( asmDir, _cookieFile ) );
            }
            catch { }
         }
         foreach( string c in candidates )
         {
            try
            {
               if( System.IO.File.Exists( c ) ) return c;
            }
            catch { }
         }
         return null;
      }

      /// <summary>清除文件型 Cookie 及密钥缓存，使下次请求重新读取文件（半自动刷新）。</summary>
      private void ResetCookieFromFile()
      {
         _cookieHeader = "";
         _cookieFromFile = false;
         _ynmtSecret = null;
         _ynmtAesKey = null;
         _ynmtAesIv = null;
         _ynmtLastFetch = double.MinValue;
         _ynmtLastAttempt = double.MinValue;
      }

      /// <summary>弹窗提示（无桌面/无 Forms 环境自动降级为写文件提示）。</summary>
      private void PromptRefreshCookie()
      {
         string msg = "YoudaoTranslate：YNMT Cookie 已失效或网络异常。\n\n"
                    + "请双击《获取Cookie一键.bat》刷新 Cookie，\n"
                    + "完成后回到游戏继续即可（自动读取新配置）。";
         try
         {
            // 反射调用 MessageBox，避免静态依赖 System.Windows.Forms
            var forms = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault( a => a.GetName().Name == "System.Windows.Forms" )
                        ?? System.Reflection.Assembly.Load( "System.Windows.Forms" );
            var mb = forms.GetType( "System.Windows.Forms.MessageBox" );
            var show = mb.GetMethod( "Show", new[] { typeof( string ), typeof( string ) } );
            show.Invoke( null, new object[] { msg, "YoudaoTranslate" } );
            return;
         }
         catch
         {
         }
         try
         {
            System.IO.File.AppendAllText( "YoudaoTranslate.Cookie提示.txt", DateTime.Now.ToString( "yyyy-MM-dd HH:mm:ss" ) + " " + msg.Replace( "\n", " " ) + Environment.NewLine );
         }
         catch
         {
         }
      }

      private IEnumerator YnmtFetchKeysCoroutine()
      {
         _ynmtLastAttempt = _clock.Elapsed.TotalSeconds;
         _lastKeyError = null;

         long t = CurrentTimeMillis();
         string sign = Md5Hex( "client=fanyideskweb&mysticTime=" + t + "&product=webfanyi&key=" + LegacyConstSign );
         string url = LegacyKeyUrl
            + "?keyid=" + LegacyKeyGetterKeyId
            + "&sign=" + sign
            + "&client=fanyideskweb&product=webfanyi&appVersion=1.0.0&vendor=web"
            + "&pointParam=client,mysticTime,product&mysticTime=" + t
            + "&keyfrom=fanyi.web&mid=1&screen=1&model=1&network=wifi&abtest=0&yduuid=" + _yduuid;

         string responseText = null;
         string error = null;
         var task = Task.Run( () => HttpGet( url ) );
         while( !task.IsCompleted )
         {
            yield return null;
         }
         if( task.IsFaulted || task.IsCanceled )
         {
            Exception inner = task.Exception != null && task.Exception.InnerException != null ? task.Exception.InnerException : null;
            error = ( inner == null ? "请求异常" : inner.GetType().Name + ": " + inner.Message );
         }
         else
         {
            responseText = task.Result;
         }

         if( error != null )
         {
            _lastKeyError = error;
            yield break;
         }
         if( string.IsNullOrEmpty( responseText ) )
         {
            _lastKeyError = "空响应";
            yield break;
         }

         // 解析（不依赖 Newtonsoft，避免个别环境下 TypeLoad；密钥 JSON 结构固定，用正则提取）
         try
         {
            bool codeOk = System.Text.RegularExpressions.Regex.IsMatch( responseText, "\"code\"\\s*:\\s*0" );
            if( codeOk )
            {
               string secret = ExtractJsonStringField( responseText, "secretKey" );
               string aesKey = ExtractJsonStringField( responseText, "aesKey" );
               string aesIv = ExtractJsonStringField( responseText, "aesIv" );
               if( !string.IsNullOrEmpty( secret ) && !string.IsNullOrEmpty( aesKey ) && !string.IsNullOrEmpty( aesIv ) )
               {
                  _ynmtSecret = secret;
                  _ynmtAesKey = aesKey;
                  _ynmtAesIv = aesIv;
                  _ynmtLastFetch = _clock.Elapsed.TotalSeconds;
                  _lastKeyError = null;
               }
               else
               {
                  _lastKeyError = "data 缺少字段（secret/aesKey/aesIv）";
               }
            }
            else
            {
               string msg = responseText.Length > 150 ? responseText.Substring( 0, 150 ) : responseText;
               _lastKeyError = "服务端返回: " + msg;
            }
         }
         catch( Exception ex )
         {
            _lastKeyError = "解析失败: " + ex.GetType().Name + ": " + ex.Message;
         }
      }

      private static string ExtractJsonStringField( string json, string fieldName )
      {
         var m = System.Text.RegularExpressions.Regex.Match( json, "\"" + System.Text.RegularExpressions.Regex.Escape( fieldName ) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"" );
         if( !m.Success ) return null;
         // 密钥字段为 ASCII（ydsecret://...），无需复杂反转义
         return m.Groups[ 1 ].Value.Replace( "\\\"", "\"" ).Replace( "\\\\", "\\" );
      }

      private string YnmtDecrypt( string base64UrlText )
      {
         string aesKeyStr = _ynmtAesKey ?? "ydsecret://query/key/B*RGygVywfNBwpmBaZg*WT7SIOUP2T0C9WHMZN39j^DAdaZhAnxvGcCY6VYFwnHl";
         string aesIvStr = _ynmtAesIv ?? "ydsecret://query/iv/C@lZe2YzHtZ2CYgaXKSVfsb7Y4QWHjITPPZ0nQp87fBeJ!Iv6v^6fvi2WN@bYpJ4";

         string b64 = base64UrlText.Replace( '-', '+' ).Replace( '_', '/' );
         while( b64.Length % 4 != 0 ) b64 += "=";
         byte[] encrypted = Convert.FromBase64String( b64 );

         byte[] key = Md5Bytes( Encoding.UTF8.GetBytes( aesKeyStr ) );
         byte[] iv = Md5Bytes( Encoding.UTF8.GetBytes( aesIvStr ) );

         using( var aes = Aes.Create() )
         {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using( var decryptor = aes.CreateDecryptor() )
            using( var ms = new System.IO.MemoryStream( encrypted ) )
            using( var cs = new CryptoStream( ms, decryptor, CryptoStreamMode.Read ) )
            using( var reader = new System.IO.StreamReader( cs, Encoding.UTF8 ) )
            {
               return reader.ReadToEnd();
            }
         }
      }

      private static string ExtractYnmtText( string decryptedJson )
      {
         try
         {
            JObject json = JObject.Parse( decryptedJson );
            var results = json[ "translateResult" ] as JArray;
            if( results == null ) return null;
            var sb = new StringBuilder();
            foreach( JToken block in results )
            {
               if( block is JArray )
               {
                  foreach( JToken item in block )
                  {
                     JToken tgt = item[ "tgt" ];
                     if( tgt != null && tgt.Type == JTokenType.String ) sb.Append( tgt.Value<string>() );
                  }
               }
            }
            return sb.Length > 0 ? sb.ToString() : null;
         }
         catch
         {
            // 兜底：直接用正则拼接所有 tgt（含 \uXXXX 转义时做最简反转义）
            var sb = new StringBuilder();
            var regex = new System.Text.RegularExpressions.Regex( "\"tgt\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"" );
            foreach( System.Text.RegularExpressions.Match m in regex.Matches( decryptedJson ) )
            {
               string raw = m.Groups[ 1 ].Value;
               try
               {
                  raw = Newtonsoft.Json.Linq.JToken.Parse( "\"" + raw + "\"" ).Value<string>();
               }
               catch
               {
                  raw = raw.Replace( "\\\"", "\"" ).Replace( "\\\\", "\\" );
               }
               sb.Append( raw );
            }
            return sb.Length > 0 ? sb.ToString() : null;
         }
      }

      private static byte[] Md5Bytes( byte[] input )
      {
         using( MD5 md5 = MD5.Create() )
         {
            return md5.ComputeHash( input );
         }
      }

      // ------------------------------------------------------------------
      // 密钥获取（自带 HTTP）
      // ------------------------------------------------------------------

      private bool ShouldRefreshKeys()
      {
         if( _manualKeys ) return false;
         double now = _clock.Elapsed.TotalSeconds;
         bool noKeys = string.IsNullOrEmpty( _secretKey ) || string.IsNullOrEmpty( _token );
         if( noKeys )
         {
            return now - _lastKeyAttemptTime >= 10;
         }
         return now - _lastKeyFetchTime >= _keyRefreshSeconds;
      }

      private IEnumerator FetchKeysCoroutine()
      {
         _lastKeyAttemptTime = _clock.Elapsed.TotalSeconds;
         _lastKeyError = null;

         var extra = new Dictionary<string, string> { { "targetKeyid", _translateKeyId } };
         if( !string.IsNullOrEmpty( _token ) ) extra[ "token" ] = _token;

         var signed = BuildSignedParams( _keyGetterConstSign, _keyGetterKeyId, extra );
         string query = BuildQueryString( signed );

         string responseText = null;
         string error = null;
         var task = Task.Run( () => HttpPost( _keyEndpointUrl + "?" + query, null, null ) );
         while( !task.IsCompleted )
         {
            yield return null;
         }
         if( task.IsFaulted || task.IsCanceled )
         {
            Exception inner = task.Exception != null && task.Exception.InnerException != null ? task.Exception.InnerException : null;
            error = ( inner == null ? "请求异常" : inner.GetType().Name + ": " + inner.Message )
                  + ( inner != null && !string.IsNullOrEmpty( inner.StackTrace ) ? " | " + inner.StackTrace : "" );
         }
         else
         {
            responseText = task.Result;
         }

         if( error != null )
         {
            _lastKeyError = error;
            yield break;
         }
         if( string.IsNullOrEmpty( responseText ) )
         {
            _lastKeyError = "空响应";
            yield break;
         }

         try
         {
            JObject json = JObject.Parse( responseText );
            if( (int?)json[ "code" ] == 0 )
            {
               JToken dataNode = json[ "data" ];
               string secret = dataNode == null ? null : (string)dataNode[ "secretKey" ];
               string token = dataNode == null ? null : (string)dataNode[ "token" ];
               if( !string.IsNullOrEmpty( secret ) && !string.IsNullOrEmpty( token ) )
               {
                  _secretKey = secret;
                  _token = token;
                  _lastKeyFetchTime = _clock.Elapsed.TotalSeconds;
                  _lastKeyError = null;
               }
               else
               {
                  _lastKeyError = "data 缺少 secretKey/token";
               }
            }
            else
            {
               string msg = responseText.Length > 120 ? responseText.Substring( 0, 120 ) : responseText;
               _lastKeyError = "服务端返回: " + msg;
            }
         }
         catch( Exception ex )
         {
            _lastKeyError = "响应解析失败: " + ex.GetType().Name + ": " + ex.Message;
         }
      }

      private void InvalidateKeys()
      {
         if( _manualKeys ) return;
         _secretKey = null;
         _token = null;
         _lastKeyFetchTime = double.MinValue;
      }

      // ------------------------------------------------------------------
      // HTTP 层（HttpWebRequest，兼容 chunked/SSE）
      // ------------------------------------------------------------------

      private string HttpGet( string url )
      {
         ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

         var request = (HttpWebRequest)WebRequest.Create( url );
         request.Method = "GET";
         request.Timeout = 30000;
         request.ReadWriteTimeout = 30000;
         request.UserAgent = UserAgent;
         request.Accept = "application/json, text/plain, */*";
         request.Referer = Referer;
         request.Headers[ HttpRequestHeader.AcceptLanguage ] = "zh-CN,zh;q=0.9,en;q=0.8";
         request.Headers[ "Origin" ] = "https://fanyi.youdao.com";
         ApplyCookie( request );

         using( var response = (HttpWebResponse)request.GetResponse() )
         {
            using( var reader = new System.IO.StreamReader( response.GetResponseStream(), Encoding.UTF8 ) )
            {
               return reader.ReadToEnd();
            }
         }
      }

      private void ApplyCookie( HttpWebRequest request )
      {
         if( string.IsNullOrEmpty( _cookieHeader ) ) return;
         // Cookie 受限头：改用 CookieContainer；支持 "k=v; k2=v2" 多 Cookie（YNMT 需要完整会话）
         var container = new CookieContainer();
         foreach( string part in _cookieHeader.Split( ';' ) )
         {
            string seg = part.Trim();
            if( seg.Length == 0 ) continue;
            int eq = seg.IndexOf( '=' );
            if( eq <= 0 ) continue;
            string name = seg.Substring( 0, eq ).Trim();
            string value = seg.Substring( eq + 1 ).Trim();
            if( name.Length == 0 || value.Length == 0 ) continue;
            try
            {
               container.Add( new Uri( "https://dict.youdao.com" ), new Cookie( name, value ) { Domain = "youdao.com" } );
            }
            catch
            {
               // 忽略无法解析的单个 cookie
            }
         }
         request.CookieContainer = container;
      }

      private string HttpPost( string url, string body, string contentType )
      {
         ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

         var request = (HttpWebRequest)WebRequest.Create( url );
         request.Method = "POST";
         request.Timeout = 30000;
         request.ReadWriteTimeout = 30000;
         request.ServicePoint.Expect100Continue = false;
         request.UserAgent = UserAgent;
         request.Accept = "application/json, text/plain, */*";
         request.Referer = Referer;
         request.Headers[ HttpRequestHeader.AcceptLanguage ] = "zh-CN,zh;q=0.9,en;q=0.8";
         request.Headers[ "Origin" ] = "https://fanyi.youdao.com";
         ApplyCookie( request );

         if( body != null )
         {
            request.ContentType = contentType;
            byte[] data = Encoding.UTF8.GetBytes( body );
            request.ContentLength = data.Length;
            using( var stream = request.GetRequestStream() )
            {
               stream.Write( data, 0, data.Length );
            }
         }
         else
         {
            request.ContentLength = 0;
         }

         using( var response = (HttpWebResponse)request.GetResponse() )
         {
            using( var reader = new System.IO.StreamReader( response.GetResponseStream(), Encoding.UTF8 ) )
            {
               return reader.ReadToEnd();
            }
         }
      }

      // ------------------------------------------------------------------
      // 有道 v3 参数签名（与前端 genParamV3 一致）
      // ------------------------------------------------------------------

      private Dictionary<string, string> BuildSignedParams( string secret, string keyId, Dictionary<string, string> extra )
      {
         var map = new Dictionary<string, string>( StringComparer.Ordinal )
         {
            { "product", Product },
            { "appVersion", AppVersion },
            { "client", Client },
            { "mid", "1" },
            { "vendor", "web" },
            { "screen", "1" },
            { "model", "1" },
            { "imei", "1" },
            { "network", "wifi" },
            { "keyfrom", Keyfrom },
            { "keyid", keyId },
            { "mysticTime", CurrentTimeMillis().ToString() },
            { "yduuid", _yduuid },
            { "abtest", "0" },
         };

         if( extra != null )
         {
            foreach( var kv in extra )
            {
               map[ kv.Key ] = kv.Value;
            }
         }

         var keys = map.Where( kv => !string.IsNullOrEmpty( kv.Value ) )
                       .Select( kv => kv.Key )
                       .OrderBy( k => k, StringComparer.Ordinal )
                       .ToList();
         keys.Add( "key" );
         map[ "key" ] = secret;

         string signInput = string.Join( "&", keys.Select( k => k + "=" + map[ k ] ) );
         string sign = Md5Hex( signInput );

         map.Remove( "key" );
         map[ "sign" ] = sign;
         map[ "pointParam" ] = string.Join( ",", keys );

         return map;
      }

      // ------------------------------------------------------------------
      // 解析与工具
      // ------------------------------------------------------------------

      private static string ParseSseTranslation( string sseText )
      {
         if( string.IsNullOrEmpty( sseText ) ) return null;

         // 方式一：按 SSE 事件块解析
         var builder = new StringBuilder();
         string currentEvent = null;
         bool anyMessage = false;

         string normalized = sseText.Replace( "\r\n", "\n" ).Replace( '\r', '\n' );
         string[] lines = normalized.Split( '\n' );
         foreach( string rawLine in lines )
         {
            string line = rawLine.Trim();
            if( line.Length == 0 )
            {
               currentEvent = null;
               continue;
            }
            if( line.StartsWith( "event:", StringComparison.Ordinal ) )
            {
               currentEvent = line.Substring( 6 ).Trim();
               continue;
            }
            if( line.StartsWith( "data:", StringComparison.Ordinal ) )
            {
               string payload = line.Substring( 5 ).Trim();
               if( payload.Length == 0 ) continue;
               if( string.Equals( currentEvent, "message", StringComparison.Ordinal ) )
               {
                  if( TryGetTransIncre( payload, out string piece ) && !string.IsNullOrEmpty( piece ) )
                  {
                     anyMessage = true;
                     builder.Append( piece );
                  }
               }
            }
         }

         if( !anyMessage || builder.Length == 0 )
         {
            // 方式二（兜底）：直接从全文提取所有 transIncre 字符串，按顺序拼接
            var regex = new System.Text.RegularExpressions.Regex( "\"transIncre\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", System.Text.RegularExpressions.RegexOptions.Compiled );
            var fallback = new StringBuilder();
            foreach( System.Text.RegularExpressions.Match m in regex.Matches( sseText ) )
            {
               if( TryUnescape( m.Groups[ 1 ].Value, out string unescaped ) )
               {
                  fallback.Append( unescaped );
               }
            }
            if( fallback.Length > 0 )
            {
               return fallback.ToString();
            }
            return null;
         }

         return builder.ToString();
      }

      private static bool TryGetTransIncre( string payload, out string piece )
      {
         piece = null;
         try
         {
            JObject obj = JObject.Parse( payload );
            JToken t = obj[ "transIncre" ];
            if( t == null || t.Type != JTokenType.String ) return false;
            piece = t.Value<string>();
            return true;
         }
         catch
         {
            return false;
         }
      }

      private static bool TryUnescape( string raw, out string value )
      {
         value = null;
         try
         {
            value = JToken.Parse( "\"" + raw + "\"" ).Value<string>();
            return true;
         }
         catch
         {
            // 非标准转义时原样返回（去掉一层最外引号影响不大）
            value = raw;
            return !string.IsNullOrEmpty( raw );
         }
      }

      private static bool IsErrorJson( string text, out int code )
      {
         code = -1;
         string trimmed = text.TrimStart();
         if( !trimmed.StartsWith( "{", StringComparison.Ordinal ) ) return false;
         try
         {
            JObject obj = JObject.Parse( trimmed );
            JToken c = obj[ "code" ];
            if( c == null ) return false;
            if( c.Type == JTokenType.Integer )
            {
               code = (int)c;
            }
            else if( c.Type == JTokenType.String )
            {
               int parsed;
               code = int.TryParse( (string)c, out parsed ) ? parsed : -1;
            }
            return true;
         }
         catch
         {
            return false;
         }
      }

      private static string FixLanguage( string lang )
      {
         if( string.IsNullOrWhiteSpace( lang ) ) return lang;

         string l = lang.Trim();
         string lower = l.ToLowerInvariant();

         if( lower == "zh" || lower.StartsWith( "zh-cn" ) || lower.StartsWith( "zh-hans" ) || lower.StartsWith( "zh-chs" ) || lower.StartsWith( "zh-sg" ) )
         {
            return "zh-CHS";
         }
         if( lower.StartsWith( "zh-tw" ) || lower.StartsWith( "zh-hant" ) || lower.StartsWith( "zh-cht" ) || lower.StartsWith( "zh-hk" ) || lower.StartsWith( "zh-mo" ) )
         {
            return "zh-CHT";
         }

         int dash = lower.IndexOf( '-' );
         string baseCode = dash > 0 ? lower.Substring( 0, dash ) : lower;

         switch( baseCode )
         {
            case "auto":
            case "en": case "ja": case "ko": case "fr": case "de": case "es": case "ru":
            case "it": case "pt": case "ar": case "th": case "vi": case "id": case "nl":
            case "ms": case "my": case "km": case "kk": case "ca": case "ro": case "ne":
            case "sv": case "eo": case "hu": case "hi":
               return baseCode;
            default:
               return l;
         }
      }

      private static string JsEncodeComponent( string value )
      {
         var builder = new StringBuilder( value.Length * 2 );
         byte[] bytes = Encoding.UTF8.GetBytes( value );
         foreach( byte b in bytes )
         {
            char c = (char)b;
            if( ( c >= 'A' && c <= 'Z' ) || ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' )
                || c == '-' || c == '_' || c == '.' || c == '~' || c == '!'
                || c == '*' || c == '\'' || c == '(' || c == ')' )
            {
               builder.Append( c );
            }
            else
            {
               builder.Append( '%' ).Append( b.ToString( "X2" ) );
            }
         }
         return builder.ToString();
      }

      private static string BuildQueryString( IEnumerable<KeyValuePair<string, string>> parameters )
      {
         return string.Join( "&", parameters.Select( kv => kv.Key + "=" + Uri.EscapeDataString( kv.Value ?? "" ) ) );
      }

      private static string Md5Hex( string input )
      {
         using( MD5 md5 = MD5.Create() )
         {
            byte[] hash = md5.ComputeHash( Encoding.UTF8.GetBytes( input ) );
            var builder = new StringBuilder( hash.Length * 2 );
            foreach( byte b in hash )
            {
               builder.Append( b.ToString( "x2" ) );
            }
            return builder.ToString();
         }
      }

      private static long CurrentTimeMillis()
      {
         return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
      }

      private static string NewYduuid()
      {
         var guid = Guid.NewGuid().ToString( "N" ) + Guid.NewGuid().ToString( "N" );
         return guid.Substring( 0, 32 );
      }
   }
}
