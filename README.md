# XUnity.AutoTranslator.YoudaoTranslateEndpoint

[XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator) 有道在线翻译（Web 端点）实现
—— **2026-09 升级版**（对接有道新版 `dict-trans.youdao.com` webmain/SSE 接口）。

## 为什么原来的 Dll 不能用了？

有道在 2025 年末更新了网页翻译体系：

1. 旧的 `dict.youdao.com/webtranslate`（代码里硬编码 `fsdsogkndfokasodnaso` 常量密钥 + 固定 AES Key）**已被服务端关闭**，签名常量与 keyid 全部失效（实测返回 `request error` / `签名验证失败`）。
2. 新网页端改走 **动态密钥 + SSE 流式** 协议：
   - `POST https://dict-trans.youdao.com/translate/key` —— 用产品常量密钥（前端 app.js 内置）换一次性/周期性下发的 `secretKey` + `token`；
   - `POST https://dict-trans.youdao.com/webtranslate/sse` —— 用 `secretKey` 做 v3 排序 MD5 签名，SSE 事件流逐片返回 `transIncre`，拼起来即译文；
   - 签名密钥、AES 密钥不再写死在客户端，**服务端动态下发**。

本仓库已按新协议完整重写（签名、密钥获取/缓存/刷新、SSE 分片解析均已对真实线上接口逐项验证）。

## 使用方法

1. 把编译产物 `YoudaoTranslate.dll` 放到 XUnity.AutoTranslator 插件目录的 `Translators` 文件夹下。
2. `AutoTranslatorConfig.ini` 里翻译端点选 `YoudaoTranslate`。
3. 可选配置（写在 `AutoTranslatorConfig.ini` 的 `[Youdao]` 段）：

```ini
[Youdao]
; 可选：去 https://fanyi.youdao.com 按 F12 -> 存储(Cookie)，复制 OUTFOX_SEARCH_USER_ID 的值。
; 留空也能用（匿名单次限制较多时建议填上，降低被限流概率）。
Cookie=
; 相邻翻译请求的最小间隔（秒）。新版接口是匿名 LLM 通道，限流很严，建议 >= 1.0。
DelaySeconds=1.0
; 动态密钥刷新间隔（秒）。默认 600 秒刷一次；若出现频繁“签名验证失败”可调小（如 60）。
KeyRefreshSeconds=600
; 设备指纹 id（32 位十六进制），一般无需修改，留空自动生成。多个账号/多开游戏建议各不相同。
Yduuid=
; 翻译引擎：llmLite（免费匿名可用）。llmPro 需要会员且部分语对受限，一般不要动。
Model=llmLite
; 引擎模式：lite=webmain/llmLite（默认，免 Cookie）；ynmt=网页 TextTranslate 同款 YNMT 引擎（译文与网页一致，需会话 Cookie）
Mode=lite
; CookieFile：半自动模式使用的 Cookie 配置文件（Mode=ynmt 且 Cookie 留空时自动读取）。
; 把《获取Cookie一键.bat》生成的 youdao-Cookie配置.txt 放到游戏根目录 或 dll 同目录即可；
; 失效后插件会自动重读该文件（重跑一键脚本覆盖即可恢复），仍失败则弹窗提示。
CookieFile=youdao-Cookie配置.txt
; CookiePrompt：失效时是否弹窗提示刷新（true/false，默认 true）
CookiePrompt=true
; Cookie 补充说明：YNMT 模式需要 .youdao.com 的会话 Cookie（以 OUTFOX_SEARCH_USER_ID 为主，无需登录）。
; 获取：直接双击随包文件《获取Cookie一键.bat》（自动打开无头浏览器取 Cookie，
; 生成 youdao-Cookie配置.txt 并复制到剪贴板），把 [Youdao] 三行贴进 ini 即可。
; 该 Cookie 与出口 IP 绑定，换网络或长时间失效后（日志报 YNMT 取密钥失败/500）重新双击一次。
; ---- 以下为有道前端常量，仅当有道轮换导致全部请求“签名验证失败”时按“维护指引”更新 ----
KeyGetterKeyId=translate-webmain-key-getter
KeyGetterConstSign=kSy5gtKA4yRUxAVPJPrdYKZ0jBKyd3t1
TranslateKeyId=translate-webfanyi-webmain
; ---- 可选：覆盖接口地址（一般不需要；用于本地自测或有道迁移域名时）----
KeyEndpoint=https://dict-trans.youdao.com/translate/key
TranslateEndpoint=https://dict-trans.youdao.com/webtranslate/sse

; ---- 可选：手动注入密钥（推荐，绕过取密钥接口的风控）----
; 当日志报“有道取密钥失败/403”时，用浏览器打开 https://fanyi.youdao.com（保持页面为登录/访问状态），
; 按 F12 -> Console 粘贴执行随包文件《获取密钥-控制台脚本.txt》里的脚本，
; 把输出的 secretKey 与 token 分别填到下面两行即可（长时效，失效后重新执行一次）。
SecretKey=
Token=
```

## 测试编译产物

**方式一（推荐，可离线自检）：** 仓库配套了一个独立自测程序 `YoudaoSelfTest`，在无 Unity 环境下按 AutoTranslator 官方 TranslatorTest 的驱动方式加载并运行编译出的 `YoudaoTranslate.dll`：

```bat
:: 本地模拟模式：用本地假服务器完整走一遍 取密钥 -> 签名 -> SSE 解析 的代码路径（无需联网）
XUnity.AutoTranslator.Plugin.Core.Tests.exe -mock

:: 真实联网模式：直接对有道真实服务器翻译（注意：短时间大量请求会触发有道对该 IP 的风控）
XUnity.AutoTranslator.Plugin.Core.Tests.exe "Hello world" en zh-CHS
```

只要 `-mock` 输出 `ALL TESTS PASSED`，即代表 dll 的签名/请求/解析链路正确，可以直接放进游戏。

**方式二（官方工程）：** XUnity.AutoTranslator 仓库自带 xunit 翻译器测试工程 `test/XUnity.AutoTranslator.Plugin.Core.Tests`（`TranslatorTest<TEndpoint>` + `CoroutineSimulator`），把 `YoudaoTranslateTest : TranslatorTest<YoudaoTranslateEndpoint>` 加进去即可复用官方回归框架。

## 工作原理（新版协议）

```
fanyi.youdao.com 前端 app.js 内置常量:
   keyGetterKeyId = translate-webmain-key-getter
   constSign      = kSy5gtKA4yRUxAVPJPrdYKZ0jBKyd3t1

1) 取密钥（首次或超龄时）:
   POST dict-trans.youdao.com/translate/key?<params>
   params: 产品/设备参数 + keyid=keyGetterKeyId + targetKeyid=translate-webfanyi-webmain
   sign    = MD5( 所有非空参数按 key 字典序 k=v 用 & 连接, 末尾追加 &key={constSign} )
   pointParam = 参与签名的 key 列表 + "key"（逗号连接）
   -> 返回 { code:0, data:{ secretKey, token } }

2) 翻译:
   POST dict-trans.youdao.com/webtranslate/sse   (x-www-form-urlencoded)
   参数多出: i(文本, encodeURIComponent 风格编码), from, to,
             signSecretKey={secretKey}, keyId=translate-webfanyi-webmain,
             token, source=webmain, modelName=llmLite, useTerm=false
   sign 同上但末尾 key 用 secretKey
   -> SSE 文本流, 累加 event:message 的 data.transIncre 即完整译文

3) 本插件在每次翻译请求前自动：限速 -> 按需刷新密钥（缓存在内存，单次会话内复用）
```

## 编译

工程结构与原来一致，需要与本仓库平级的源码目录：

```
你的工作目录/
├── XUnity.AutoTranslator/            # bbepis/XUnity.AutoTranslator 源码（master）
│   └── src/
│       ├── XUnity.AutoTranslator.Plugin.Core/...   # 端点依赖的核心工程
│       └── XUnity.Common/...                        # 公共依赖（在同一个仓库内）
└── XUnity.AutoTranslator.YoudaoTranslateEndpoint/   # 本仓库
    └── YoudaoTranslate/YoudaoTranslate.csproj       # net471
```

用 Visual Studio 打开后编译 **Release**，`YoudaoTranslate.dll` 会自动输出到 `dist\Translators\`。

> 说明：XUnity.AutoTranslator.Plugin.Core 是 `net35;net6.0` 多目标工程，端点（net471）会自动选用 net35 产物；请使用与游戏内 XUnity.AutoTranslator 版本一致的源码提交来编译，避免 API 不匹配。
>
> 也可直接把 `YoudaoTranslate/YoudaoTanslateEndpoint.cs` 拷入你自己的 XUnity 端点工程（保持命名空间与类名不变）。

## 维护指引（重要）

有道会不定期轮换前端常量（历史上 keyid 带年份后缀：`webfanyi-key-getter-2025` → `translate-webmain-key-getter`）。当出现**全部**翻译失败、日志报"签名验证失败"时，按下面步骤提取新常量，改到 ini 即可（无需改代码）：

1. 浏览器打开 `https://fanyi.youdao.com/#/TextTranslate`，F12 → Network，做一次翻译；
2. 找到 `dict-trans.youdao.com/translate/key?...` 请求：
   - URL 里的 `keyid` / `targetKeyid` → 对应 `KeyGetterKeyId` / `TranslateKeyId`；
   - 新常量密钥：抓 `https://shared.ydstatic.com/dict/translation-website/<版本>/js/app.*.js`，搜索 `translate-webmain-key-getter`，其附近的 `signSecretKey`/`constSign` 字符串即为新 `KeyGetterConstSign`；
3. 把三个新值写进 `AutoTranslatorConfig.ini` 的 `[Youdao]` 段。

## 免责声明与提示

- 本实现属于对有道网页端公开接口的逆向适配，仅供学习交流，请勿高频滥用；接口随时可能再次变更。
- 新通道为匿名 LLM 轻量翻译（llmLite），质量与延迟以实际为准；游戏批量翻译建议保持 `DelaySeconds >= 1`。
- 若出现偶发一行翻译失败，属正常限流/令牌轮换，下一行会自动恢复；持续大面积失败时参考上方维护指引。
