param(
    [string]$Expression = "1",
    [int]$Port = 9222,
    [switch]$Screenshot,
    [string]$Out = "$env:TEMP\cdp-shot.png"
)

$ErrorActionPreference = "Stop"
$list = Invoke-WebRequest "http://127.0.0.1:$Port/json" -UseBasicParsing | ForEach-Object { $_.Content } | ConvertFrom-Json
$page = $list | Where-Object { $_.type -eq "page" } | Select-Object -First 1
if (-not $page) { throw "no page target" }

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ct = [System.Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, $ct).Wait()

function Invoke-Cdp([hashtable]$payload) {
    $global:__msgId = $global:__msgId + 1
    $payload["id"] = $global:__msgId
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object System.ArraySegment[byte] -ArgumentList @(, $bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait()
    # собираем ВСЕ чанки в список байтов, декодируем один раз в конце
    $chunks = New-Object System.Collections.Generic.List[byte]
    $buf = New-Object byte[] 4194304
    while ($true) {
        $seg2 = New-Object System.ArraySegment[byte] -ArgumentList @(, $buf)
        $res = $ws.ReceiveAsync($seg2, $ct).Result
        if ($res.Count -gt 0) {
            for ($i = 0; $i -lt $res.Count; $i++) { $chunks.Add($buf[$i]) }
        }
        if ($res.EndOfMessage) {
            $text = [System.Text.Encoding]::UTF8.GetString($chunks.ToArray())
            $obj = $text | ConvertFrom-Json
            if ($obj.id -eq $global:__msgId) { return $obj }
            $chunks.Clear()
        }
    }
}
$global:__msgId = 0

if ($Screenshot) {
    $res = Invoke-Cdp @{ method = "Page.captureScreenshot"; params = @{ format = "png" } }
    if ($res.result.data) {
        [System.IO.File]::WriteAllBytes($Out, [System.Convert]::FromBase64String($res.result.data))
        "saved: $Out"
    } else { "error: " + ($res | ConvertTo-Json -Depth 6 -Compress) }
} else {
    $res = Invoke-Cdp @{ method = "Runtime.evaluate"; params = @{ expression = $Expression; returnByValue = $true } }
    if ($res.result.result.value -ne $null) { $res.result.result.value }
    elseif ($res.result.exceptionDetails) { "EXC: " + ($res.result.exceptionDetails.exception.description) }
    else { $res | ConvertTo-Json -Depth 8 -Compress }
}
$ws.Dispose()
