[CmdletBinding()]
param([string]$BaseUrl = 'http://127.0.0.1:8790', [switch]$IncludeLive)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root '.runtime'
$null = New-Item -ItemType Directory -Force -Path $runtime
$profile = Join-Path $runtime ('browser-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $profile
$browser = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$script:socket = $null
$script:messageId = 0
$script:browserErrors = [System.Collections.Generic.List[string]]::new()
$browserProcess = $null

function Invoke-Cdp {
    param([string]$Method, [hashtable]$Parameters = @{})
    $script:messageId++
    $id = $script:messageId
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{id=$id; method=$Method; params=$Parameters} | ConvertTo-Json -Depth 30 -Compress))
    $timeout = [Threading.CancellationTokenSource]::new(240000)
    try {
        $script:socket.SendAsync([ArraySegment[byte]]::new($bytes), [Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult()
        while ($true) {
            $buffer = [byte[]]::new(65536)
            $stream = [IO.MemoryStream]::new()
            try {
                do {
                    $received = $script:socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
                    if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'DevTools socket closed.' }
                    $stream.Write($buffer, 0, $received.Count)
                } until ($received.EndOfMessage)
                $message = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json
            } finally { $stream.Dispose() }
            if ($message.method -eq 'Runtime.exceptionThrown') { $script:browserErrors.Add(($message.params.exceptionDetails | ConvertTo-Json -Depth 10 -Compress)) }
            if ($message.id -eq $id) {
                if ($message.error) { throw ($message.error | ConvertTo-Json -Compress) }
                return $message.result
            }
        }
    } finally { $timeout.Dispose() }
}
function Invoke-JavaScript {
    param([string]$Expression)
    $result = Invoke-Cdp 'Runtime.evaluate' @{expression=$Expression; awaitPromise=$true; returnByValue=$true}
    if ($result.exceptionDetails) { throw ($result.exceptionDetails | ConvertTo-Json -Depth 10 -Compress) }
    return $result.result.value
}
function Save-Screenshot {
    param([string]$Name)
    $shot = Invoke-Cdp 'Page.captureScreenshot' @{format='png'; captureBeyondViewport=$true}
    [IO.File]::WriteAllBytes((Join-Path $runtime $Name), [Convert]::FromBase64String($shot.data))
}
try {
    $browserProcess = Start-Process -FilePath $browser -PassThru -ArgumentList @(
        '--headless=new', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
        '--remote-debugging-port=0', "--user-data-dir=`"$profile`"", 'about:blank'
    ) -RedirectStandardOutput (Join-Path $runtime 'browser-stdout.log') -RedirectStandardError (Join-Path $runtime 'browser-stderr.log')
    $portFile = Join-Path $profile 'DevToolsActivePort'
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $portFile)) {
        if ($browserProcess.HasExited -or [datetime]::UtcNow -gt $deadline) { throw 'Browser failed to start DevTools.' }
        Start-Sleep -Milliseconds 100
    }
    $debugPort = (Get-Content -LiteralPath $portFile)[0]
    $targets = Invoke-RestMethod "http://127.0.0.1:$debugPort/json/list"
    $target = $targets | Where-Object { $_.type -eq 'page' -and $_.url -eq 'about:blank' } | Select-Object -First 1
    if (-not $target) { throw 'The requested about:blank browser tab was not created.' }
    $script:socket = [Net.WebSockets.ClientWebSocket]::new()
    $null = $script:socket.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $null = Invoke-Cdp 'Page.enable'
    $null = Invoke-Cdp 'Runtime.enable'
    $null = Invoke-Cdp 'Browser.setDownloadBehavior' @{behavior='allow';downloadPath=(Join-Path $runtime 'downloads')}
    $null = Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{width=1440;height=1050;deviceScaleFactor=1;mobile=$false}
    $null = Invoke-Cdp 'Page.navigate' @{url=$BaseUrl}
    $null = Invoke-JavaScript @'
(async () => {
  for (let i=0;i<200;i++) {
    if (document.getElementById("hours-result")?.textContent === "2,640") return true;
    await new Promise(r=>setTimeout(r,100));
  }
  throw new Error("Initialization timed out: " + document.body.innerText);
})()
'@
    Save-Screenshot 'executive-desktop.png'
    $checks = @(Invoke-JavaScript @'
(async () => {
  const checks = [], $ = id => document.getElementById(id);
  const check = (ok,label) => { if(!ok) throw new Error(label); checks.push(label); };
  const wait = async (fn,label) => {
    const start=performance.now();
    while(!fn()) { if(performance.now()-start>150000) throw new Error(label); await new Promise(r=>setTimeout(r,70)); }
  };
  check(document.documentElement.scrollWidth<=innerWidth,"Desktop has no horizontal overflow");
  const inventory=await (await fetch("/api/status")).json();
  check(inventory.device.installed_ram_gb===128 && $("ram-proof").textContent==="128 GB","Physical DIMM capacity is correctly shown as 128 GB");
  check($("ram-detail").textContent.includes("64 GB Windows-visible"),"Windows-visible 64 GB is labeled separately from installed memory");
  check(!/\bNPU\b/i.test(document.body.innerText),"Showcase hardware cards and footer focus on GPU, not NPU");
  $("guide-button").click(); check($("info-dialog").open,"Seller playbook opens");
  check($("dialog-content").textContent.includes("00:45"),"Playbook contains timed executive flow");
  $("close-dialog").click();
  $("device-button").click(); check($("dialog-content").textContent.includes("RTX GPU through CUDA") && !/\bNPU\b/i.test($("dialog-content").textContent),"Device evidence is GPU-focused");
  $("close-dialog").click();
  $("source-button").click(); check($("dialog-content").textContent.includes("FICTIONAL ACCOUNT"),"Original synthetic source is inspectable");
  $("close-dialog").click();
  $("mode").value="rehearsal"; $("mode").dispatchEvent(new Event("change"));
  check($("prompt").readOnly,"Authored rehearsal locks its prompt");
  for(const scene of ["brief","code"]) {
    document.querySelector(`[data-story="${scene}"]`).click();
    $("run-button").click();
    await wait(() => !$("run-button").disabled,"Rehearsal did not finish");
    check($("error").hidden,`${scene}: rehearsal has no error`);
    check($("run-state").textContent.includes("no inference"),`${scene}: rehearsal explicitly labeled`);
    check($("ttft").textContent==="Not measured",`${scene}: no invented latency`);
    check($("route").textContent==="No inference",`${scene}: no fabricated GPU inference`);
    check($("output").textContent.length>100,`${scene}: authored output visible`);
  }
  const oldCreate=URL.createObjectURL, downloads=[];
  URL.createObjectURL=blob=>{downloads.push(blob);return oldCreate(blob);};
  $("export-run").click();
  check(JSON.parse(await downloads[0].text()).mode==="rehearsal","Evidence export preserves authored execution mode");
  document.querySelector('[data-story="value"]').click();
  check(!$("value-workspace").hidden && $("ai-workspace").hidden,"Business case story switches cleanly");
  check($("capacity-result").textContent==="$158,400","Default capacity arithmetic is correct");
  check($("investment-result").textContent==="$60,000","Assumed hardware investment is correct");
  $("minutes").value="0"; $("minutes").dispatchEvent(new Event("input"));
  await wait(()=>$("hours-result").textContent==="0","Zero-gain case did not update");
  check($("capacity-result").textContent==="$0","Zero improvement produces zero capacity value");
  $("adoption").value="-1"; $("adoption").dispatchEvent(new Event("input"));
  await wait(()=>!$("calc-error").hidden,"Invalid assumption not surfaced");
  check($("export-case").disabled && $("capacity-result").textContent==="—","Invalid case cannot export stale results");
  $("reset-calc").click();
  await wait(()=>$("hours-result").textContent==="2,640","Reset failed");
  $("export-case").click();
  const business=JSON.parse(await downloads[1].text());
  check(business.assumptions.adoption===60 && business.hours===2640,"Business case export contains inputs and derived values");
  check(business.disclaimer.includes("not cash savings"),"Business case export includes boundary");
  URL.createObjectURL=oldCreate;
  document.querySelector('[data-story="brief"]').click();
  const originalFetch=window.fetch;
  window.fetch=(url,options)=>url==="/api/run" ? Promise.resolve(new Response(JSON.stringify({error:"Injected model failure"}),{status:503,headers:{"Content-Type":"application/json"}})) : originalFetch(url,options);
  $("run-button").click();
  await wait(()=>!$("run-button").disabled,"Failure did not unlock controls");
  check(!$("error").hidden && $("error").textContent.includes("Injected model failure"),"Request failures surface their actual error");
  check($("output").textContent==="" && $("export-run").disabled,"Failure does not substitute authored output or permit evidence export");
  window.fetch=originalFetch;
  document.querySelector('[data-story="brief"]').click();
  return checks;
})()
'@)
    if ($IncludeLive) {
        $liveEvidence = Invoke-JavaScript @'
(async () => {
  const checks=[], runs=[], $=id=>document.getElementById(id);
  const oldCreate=URL.createObjectURL;
  let lastBlob;
  URL.createObjectURL=blob=>{lastBlob=blob;return oldCreate(blob);};
  const wait=async(fn,label)=>{const start=performance.now();while(!fn()){if(performance.now()-start>180000)throw new Error(label);await new Promise(r=>setTimeout(r,100));}};
  $("mode").value="live";$("mode").dispatchEvent(new Event("change"));
  await wait(()=>!$("run-button").disabled,"Local model not ready");
  if($("model").options.length!==2)throw new Error("Both larger GPU model choices are required");
  for(const alias of ["qwen2.5-coder-14b","gpt-oss-20b"]){
    if($("model").value!==alias){
      $("model").value=alias;$("model").dispatchEvent(new Event("change"));
      await wait(()=>!$("run-button").disabled && !$("model").disabled,alias+" did not load");
    }
    checks.push(alias+": CUDA model selected and ready");
    for(const scene of ["brief","code"]){
    document.querySelector(`[data-story="${scene}"]`).click();
    $("run-button").click();
    await wait(()=>!$("run-button").disabled,scene+" live run timed out");
    if(!$("error").hidden)throw new Error($("error").textContent);
    if(!$("run-state").textContent.includes("Generated on this device"))throw new Error("Not a completed live run: "+$("run-state").textContent);
    if($("ttft").textContent==="Not measured"||$("ttft").textContent==="—")throw new Error("No measured TTFT");
    if($("route").textContent!=="Local · RTX GPU")throw new Error("Live route not GPU");
    if($("output").textContent.length<100)throw new Error("Insufficient model output");
    $("export-run").click();
    const record=JSON.parse(await lastBlob.text());
    if(record.mode!=="live" || record.model.provider!=="CUDAExecutionProvider" || record.model.alias!==alias)throw new Error("Evidence does not preserve selected CUDA model identity");
    if(record.device.installed_ram_gb!==128 || record.device.os_visible_ram_gb!==64)throw new Error("Evidence memory views are incorrect");
    if(record.output.includes("<|channel|>") || record.output.includes("<|message|>"))throw new Error("Raw model channels leaked into the answer");
    if(alias==="gpt-oss-20b" && record.reasoning_characters<=0)throw new Error("Expected measured hidden reasoning activity for GPT-OSS");
    runs.push(record);
    checks.push(alias+" / "+scene+": real CUDA generation with measured latency and 128 GB installed memory evidence");
    }
  }
  URL.createObjectURL=oldCreate;
  return {checks,runs};
})()
'@
        $checks += @($liveEvidence.checks)
        $liveEvidence.runs | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runtime 'live-evidence.json')
        Save-Screenshot 'executive-live-result.png'
        $null = Invoke-JavaScript @'
(async()=>{
  const model=document.getElementById("model");
  model.value="qwen2.5-coder-14b";model.dispatchEvent(new Event("change"));
  const start=performance.now();
  while(document.getElementById("run-button").disabled){
    if(performance.now()-start>120000)throw new Error("Could not restore the default 14B model");
    await new Promise(r=>setTimeout(r,100));
  }
  return true;
})()
'@
    }
    $null = Invoke-JavaScript 'document.querySelector(''[data-story="value"]'').click()'
    Save-Screenshot 'business-case.png'
    $null = Invoke-Cdp 'Page.bringToFront'
    $null = Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{width=686;height=1015;deviceScaleFactor=1;mobile=$false}
    $null = Invoke-Cdp 'Emulation.setEmulatedMedia' @{media='print'}
    $null = Invoke-JavaScript 'document.fonts.ready.then(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true)))))'
    Save-Screenshot 'executive-print-layout.png'
    $print = Invoke-Cdp 'Page.printToPDF' @{printBackground=$true;paperWidth=8.27;paperHeight=11.69;preferCSSPageSize=$true}
    [IO.File]::WriteAllBytes((Join-Path $runtime 'Executive-Handout.pdf'), [Convert]::FromBase64String($print.data))
    $pdfText = [Text.Encoding]::ASCII.GetString([Convert]::FromBase64String($print.data))
    if ($pdfText -notmatch '/Type /Pages\s*/Count 1\b') { throw 'Executive PDF must fit on one page.' }
    $checks += 'Executive print handout renders as a single-page PDF'
    $null = Invoke-Cdp 'Emulation.setEmulatedMedia' @{media='screen'}
    $null = Invoke-Cdp 'Emulation.setDeviceMetricsOverride' @{width=390;height=844;deviceScaleFactor=1;mobile=$true}
    foreach ($scene in @('value','brief','code')) {
        $null = Invoke-JavaScript "document.querySelector('[data-story=`"$scene`"]').click()"
        $layout = Invoke-JavaScript 'new Promise(r=>setTimeout(()=>r({width:innerWidth,content:document.documentElement.scrollWidth}),150))'
        if ($layout.content -gt $layout.width) { throw "Mobile overflow in $scene`: $($layout.content) > $($layout.width)" }
        $checks += "$scene mobile layout has no horizontal overflow"
    }
    Save-Screenshot 'executive-mobile.png'
    if ($script:browserErrors.Count) { throw ($script:browserErrors -join "`n") }
    $checks += 'No uncaught JavaScript errors'
    $report = @{passed=$checks.Count;checks=$checks;include_live=[bool]$IncludeLive;browser='Microsoft Edge'}
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtime $(if($IncludeLive){'browser-live-checks.json'}else{'browser-checks.json'}))
    $report | ConvertTo-Json -Depth 5
} finally {
    if ($script:socket) { $script:socket.Dispose() }
    if ($browserProcess -and -not $browserProcess.HasExited) {
        Stop-Process -Id $browserProcess.Id -ErrorAction SilentlyContinue
        $null = $browserProcess.WaitForExit(5000)
    }
    Start-Sleep -Milliseconds 500
    if (Test-Path -LiteralPath $profile) { Remove-Item -LiteralPath $profile -Recurse -Force }
}
