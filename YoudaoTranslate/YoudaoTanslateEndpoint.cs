using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Http;
using XUnity.AutoTranslator.Plugin.Core.Web;
using System.Collections;
using XUnity.AutoTranslator.Plugin.Core.Utilities;
using XUnity.Common.Utilities;
namespace YoudaoTranslate
{
   public class YoudaoTranslateEndpoint : HttpEndpoint
   {
      private static readonly string TranslateUrl = "https://dict.youdao.com/webtranslate";
      private CookieContainer _cookieContainer;
      private float _delay;
      private float _lastRequestTimestamp;
      public YoudaoTranslateEndpoint()
      {
         _cookieContainer = new CookieContainer();
      }
      public override string Id => "YoudaoTranslate";
      public override string FriendlyName => "Youdao Translate";
      public override int MaxConcurrency => 1;  // Set the appropriate value based on your requirements.
      public override int MaxTranslationsPerRequest => 1;
      private string FixLanguage( string lang )
      {
         switch( lang )
         {

            case "zh":
               return "zh-CHS";
            default:
               return lang;
         }
      }
      public override void Initialize( IInitializationContext context )
      {
         // Initialize any settings or configurations here.
         _cookieContainer.SetCookies( new Uri( "https://dict.youdao.com" ), "OUTFOX_SEARCH_USER_ID=-" + context.GetOrCreateSetting( "Youdao", "Cookie", "128580344@10.169.0.83" ) );
         _delay = context.GetOrCreateSetting( "Youdao", "DelaySeconds", 1.0f );
      }
      //public override IEnumerator OnBeforeTranslate( IHttpTranslationContext context )
      //{
      //   var realtimeSinceStartup = TimeHelper.realtimeSinceStartup;
      //   var timeSinceLast = realtimeSinceStartup - _lastRequestTimestamp;
      //   if( timeSinceLast < _delay )
      //   {
      //      var delay = _delay - timeSinceLast;
      //      var instruction = CoroutineHelper.CreateWaitForSecondsRealtime( delay );
      //      if( instruction != null )
      //         yield return instruction;
      //      else
      //      {
      //         float start = realtimeSinceStartup;
      //         var end = start + delay;
      //         while( realtimeSinceStartup < end )
      //         {
      //            yield return null;
      //         }
      //      }
      //   }
      //   _lastRequestTimestamp = TimeHelper.realtimeSinceStartup;
      //}
      public override void OnCreateRequest( IHttpRequestCreationContext context )
      {
         // Construct form data
         var formDataDict = new Dictionary<string, string>
            {
                { "from", FixLanguage(context.SourceLanguage)},
                { "to", FixLanguage(context.DestinationLanguage)},
                { "dictResult", "false" },
                { "keyid", "webfanyi" },
                { "client", "fanyideskweb" },
                { "product", "webfanyi" },
                { "appVersion", "1.0.0" },
                { "vendor", "web" },
                { "pointParam", "client,mysticTime,product" },
                { "keyfrom", "fanyi.web" },
            };
         string r = "fanyideskweb";
         string i = "webfanyi";
         string e = "fsdsogkndfokasodnaso";
         long t = (long)( DateTime.UtcNow - new DateTime( 1970, 1, 1 ) ).TotalMilliseconds;

         string p = $"client={r}&mysticTime={t}&product={i}&key={e}";
         string sign;

         using( MD5 md5 = MD5.Create() )
         {
            byte[] hashBytes = md5.ComputeHash( Encoding.UTF8.GetBytes( p ) );
            sign = BitConverter.ToString( hashBytes ).Replace( "-", "" ).ToLower();
         }
         formDataDict.Add( "i", context.UntranslatedText.Replace( "「", "\"" ).Replace( "」", "\"" ) );
         formDataDict.Add( "sign", sign );
         formDataDict.Add( "mysticTime", t.ToString() );

         string formData = string.Join( "&", formDataDict.Select( kv => $"{Uri.EscapeDataString( kv.Key )}={Uri.EscapeDataString( kv.Value )}" ) );

         var request = new XUnityWebRequest(
             "POST",
             TranslateUrl,
             formData
             );
         // Set request headers based on your Python headers
         request.Cookies = _cookieContainer;
         request.Headers[ HttpRequestHeader.Accept ] = "application/json, text/plain, */*";
         request.Headers[ HttpRequestHeader.AcceptEncoding ] = "deflate, br";
         request.Headers[ HttpRequestHeader.AcceptLanguage ] = "zh-CN,zh;q=0.9";
         request.Headers[ HttpRequestHeader.ContentType ] = "application/x-www-form-urlencoded; charset=utf-8";
         request.Headers[ HttpRequestHeader.Referer ] = "https://fanyi.youdao.com/index.html";
         request.Headers[ "Sec-Fetch-Dest" ] = "empty";
         request.Headers[ "Sec-Fetch-Mode" ] = "cors";
         request.Headers[ "Sec-Fetch-Site" ] = "same-site";
         request.Headers[ HttpRequestHeader.UserAgent ] = "Mozilla/5.0 (Windows NT 6.3; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36";
         context.Complete( request );
      }
      public override void OnExtractTranslation( IHttpTranslationExtractionContext context )
      {
         // Implement the logic to extract the translation from the response data.
         // You may refer to the Python code for guidance.
         var res = context.Response;
         var data = res.Data;
         string result = Translate( data );
         context.Complete( result );
      }
      public string Translate( string text )
      {
         string decodeKey = "ydsecret://query/key/B*RGygVywfNBwpmBaZg*WT7SIOUP2T0C9WHMZN39j^DAdaZhAnxvGcCY6VYFwnHl";
         string decodeIv = "ydsecret://query/iv/C@lZe2YzHtZ2CYgaXKSVfsb7Y4QWHjITPPZ0nQp87fBeJ!Iv6v^6fvi2WN@bYpJ4";
         byte[] key = System.Text.Encoding.UTF8.GetBytes( decodeKey );
         byte[] iv = System.Text.Encoding.UTF8.GetBytes( decodeIv );
         byte[] hashedIV = MD5Hash( iv );
         byte[] hashedKey = MD5Hash( key );
         text = text.Replace( '_', '/' ).Replace( '-', '+' );
         byte[] encryptedBytes = Convert.FromBase64String( text );
         using( AesManaged aesAlg = new AesManaged() )
         {
            aesAlg.Key = hashedKey;
            aesAlg.IV = hashedIV;

            ICryptoTransform decryptor = aesAlg.CreateDecryptor( aesAlg.Key, aesAlg.IV );
            using( MemoryStream msDecrypt = new MemoryStream( encryptedBytes ) )
            using( CryptoStream csDecrypt = new CryptoStream( msDecrypt, decryptor, CryptoStreamMode.Read ) )
            using( StreamReader srDecrypt = new StreamReader( csDecrypt ) )
            {
               string decode = srDecrypt.ReadToEnd();
               JObject jsonData = Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>( decode );
               var translateResultArray = jsonData[ "translateResult" ];
               string combinedTgt = "";
               foreach( var result in translateResultArray )
               {
                  foreach( var item in result )
                  {
                     string tgtValue = item[ "tgt" ].ToString();
                     combinedTgt += tgtValue;
                  }                
               }
               if( string.IsNullOrEmpty( combinedTgt ) )
                  return decode;
               else
                  return combinedTgt;
            }

         }
      }
      private byte[] MD5Hash( byte[] input )
      {
         using( MD5 md5 = MD5.Create() )
         {
            return md5.ComputeHash( input );
         }
      }
      private string Unpad( byte[] input, int blockSize )
      {
         int padSize = input[ input.Length - 1 ];
         int unpaddedLength = input.Length - padSize;
         byte[] unpaddedBytes = new byte[ unpaddedLength ];
         Array.Copy( input, unpaddedBytes, unpaddedLength );
         return Encoding.UTF8.GetString( unpaddedBytes );
      }
   }
}
