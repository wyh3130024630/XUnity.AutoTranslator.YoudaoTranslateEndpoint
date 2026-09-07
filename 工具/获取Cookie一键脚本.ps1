# ============================================================================
#  有道 YNMT 模式 Cookie 一键获取脚本
#  原理：启动一次无头 Edge/Chrome -> 访问 fanyi.youdao.com 与 dict.youdao.com
#        （服务器会下发 OUTFOX_SEARCH_USER_ID 与 DICT_DOCTRANS_SESSION_ID）
#        -> 通过 DevTools 协议读取全部 Cookie（含 httpOnly）
#        -> 输出可直接粘贴到 AutoTranslatorConfig.ini 的配置并复制到剪贴板。
#  用法：双击同目录的“获取Cookie一键.bat”，或右键“使用 PowerShell 运行”。
# ============================================================================

$ErrorActionPreference = 'Stop'

function Find-Browser {
    $candidates = @(
        "$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "$env:ProgramFiles(x86)\Google\Chrome\Application\chrome.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Wait-Port([int]$port, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:$port/json/version" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $true }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}

function Open-Target([int]$port, [string]$url) {
    $enc = [uri]::EscapeDataString($url)
    try {
        return Invoke-RestMethod -Method Put -Uri "http://127.0.0.1:$port/json/new?$enc" -TimeoutSec 10
    } catch {
        return Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/json/new" -ContentType 'application/json' -Body (@{ url = $url } | ConvertTo-Json) -TimeoutSec 10
    }
}

function Send-Cdp($ws, [int]$id, [string]$method, $params) {
    $obj = @{ id = $id; method = $method }
    if ($params) { $obj.params = $params }
    $json = $obj | ConvertTo-Json -Depth 6 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (,$bytes)
    [void]$ws.SendAsync($seg, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Receive-Cdp($ws) {
    $buffer = New-Object byte[] 65536
    $sb = New-Object Text.StringBuilder
    do {
        $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (,$buffer)
        $result = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
    } while (-not $result.EndOfMessage)
    return $sb.ToString() | ConvertFrom-Json
}

function Pause-If-Interactive {
    if (-not [Console]::IsInputRedirected) { Read-Host '按回车退出' }
}

Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host '  有道 Cookie 一键获取（YNMT 模式用）' -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host ''

$browser = Find-Browser
if (-not $browser) {
    Write-Host '[错误] 未找到 Edge 或 Chrome，请先安装任意一个。' -ForegroundColor Red
    Pause-If-Interactive
    exit 1
}

# 挑选空闲端口（避免上次运行残留占用）
$port = 0
for ($try = 9227; $try -le 9245; $try++) {
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $try)
    try { $probe.Start(); $probe.Stop(); $port = $try; break } catch { }
}
if ($port -eq 0) { throw '没有可用端口(9227-9245)，请先结束残留的 msedge/chrome 进程。' }
$profile = Join-Path $env:TEMP ("yd_cookie_profile_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $profile | Out-Null

$bargs = @(
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--no-sandbox', '--disable-dev-shm-usage', '--remote-allow-origins=*',
    "--remote-debugging-port=$port",
    "--user-data-dir=$profile",
    '--window-size=1280,800'
)

$proc = $null
$ws = $null
try {
    Write-Host '[1/4] 启动无头浏览器...'
    $errFile = Join-Path $env:TEMP ('yd_edge_err_' + [guid]::NewGuid().ToString('N') + '.txt')
    $proc = Start-Process -FilePath $browser -ArgumentList $bargs -PassThru -RedirectStandardError $errFile
    if (-not (Wait-Port $port 25)) {
        $diag = 'alive=' + (-not $proc.HasExited)
        try { $diag += ' exit=' + $proc.ExitCode } catch { }
        try { $diag += ' stderr: ' + ((Get-Content $errFile -Tail 8 -ErrorAction SilentlyContinue) -join ' / ') } catch { }
        throw ('浏览器调试端口未就绪。' + $diag)
    }

    Write-Host '[2/4] 访问 fanyi.youdao.com / dict.youdao.com（等待服务器下发 Cookie）...'
    $list = Invoke-RestMethod -Uri ("http://127.0.0.1:" + $port + '/json/list') -TimeoutSec 10
    $target = $list | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
    if (-not $target) { $target = $list | Select-Object -First 1 }
    if (-not $target -or -not $target.webSocketDebuggerUrl) { throw '未找到可用的页面目标' }
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    [void]$ws.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Send-Cdp $ws 1 'Page.navigate' @{ url = 'https://fanyi.youdao.com/' }
    Start-Sleep -Seconds 9
    Send-Cdp $ws 2 'Page.navigate' @{ url = 'https://dict.youdao.com/' }
    Start-Sleep -Seconds 7

    Write-Host '[3/4] 读取 Cookie（含 httpOnly）...'
    Send-Cdp $ws 3 'Network.getAllCookies' $null
    $resp = $null
    for ($i = 0; $i -lt 40; $i++) { $m = Receive-Cdp $ws; if ($m.id -eq 3) { $resp = $m; break } }
    if (-not $resp -or -not $resp.result) { throw 'CDP 未返回 Cookie 数据（可重试）' }
    $cookies = @($resp.result.cookies | Where-Object {
        $_.domain -like '*youdao.com*'
    })

    $needed = @('OUTFOX_SEARCH_USER_ID', 'OUTFOX_SEARCH_USER_ID_NCOO', 'DICT_DOCTRANS_SESSION_ID')
    $outfox = $cookies | Where-Object { $_.name -eq 'OUTFOX_SEARCH_USER_ID' }

    if (-not $outfox) {
        throw '未取到 OUTFOX_SEARCH_USER_ID，网络可能无法访问有道，请重试。'
    }

    # 组装 Cookie 串：关键字段优先、按名去重，其余 .youdao.com 的也带上
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' -ArgumentList ([System.StringComparer]::OrdinalIgnoreCase)
    $ordered = New-Object System.Collections.Generic.List[string]
    foreach ($n in $needed) {
        $hit = $cookies | Where-Object { $_.name -eq $n } | Select-Object -First 1
        if ($hit -and $seen.Add($hit.name)) { [void]$ordered.Add($hit.name + '=' + $hit.value) }
    }
    foreach ($c in $cookies) {
        if ($seen.Add($c.name)) { [void]$ordered.Add($c.name + '=' + $c.value) }
    }
    $cookieLine = $ordered -join '; '

    $iniSnippet = @(
        '[Youdao]',
        'Mode=ynmt',
        "Cookie=$cookieLine",
        'DelaySeconds=1.0'
    )

    $outFile = Join-Path $PSScriptRoot 'youdao-Cookie配置.txt'
    $iniSnippet | Set-Content -Path $outFile -Encoding UTF8
    try { $cookieLine | Set-Clipboard } catch { }

    Write-Host '[4/4] 完成！' -ForegroundColor Green
    Write-Host ''
    Write-Host '已复制到剪贴板，并保存到：' -ForegroundColor Cyan
    Write-Host "  $outFile" -ForegroundColor Cyan
    Write-Host ''
    Write-Host '在游戏 AutoTranslatorConfig.ini 的 [Youdao] 段粘贴下面三行即可：' -ForegroundColor Yellow
    Write-Host '----------------------------------------' -ForegroundColor Yellow
    $iniSnippet | ForEach-Object { Write-Host $_ -ForegroundColor White }
    Write-Host '----------------------------------------' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '提示：Cookie 与出口 IP 绑定（无需登录）；换网络/长时间使用后失效时，' -ForegroundColor DarkGray
    Write-Host '重新双击一次本脚本即可（日志报 YNMT 取密钥失败/500 即为失效）。' -ForegroundColor DarkGray
}
catch {
    Write-Host ''
    Write-Host ('[失败] ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host '请确认网络能打开 fanyi.youdao.com 后重试。' -ForegroundColor Red
}
finally {
    try { if ($ws) { $ws.Dispose() } } catch { }
    try { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } } catch { }
    try { Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Pause-If-Interactive
