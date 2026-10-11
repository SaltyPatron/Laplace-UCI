#requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installation,[string]$Enrollment,[switch]$Render,[switch]$Plan)
$ErrorActionPreference='Stop'
$c=Get-Content -LiteralPath $Installation -Raw | ConvertFrom-Json
foreach($name in 'directory','publish','engine','bao') {
    if(-not [IO.Path]::IsPathFullyQualified($c.$name)){throw "Declare an absolute $name path."}
}
if($c.host -ne $env:COMPUTERNAME.ToLowerInvariant()){throw 'The UCI installation belongs to another host.'}
if($c.site -notmatch '^[a-zA-Z0-9-]+$' -or $c.hostname -notmatch '^[a-zA-Z0-9.-]+$'){throw 'Invalid IIS identity.'}
if(-not $c.address.StartsWith('https://') -or $c.runtime_record -notmatch '^kv/data/[a-zA-Z0-9_/-]+$' -or $c.certificate_record -notmatch '^kv/data/[a-zA-Z0-9_/-]+$'){throw 'Declare HTTPS OpenBao and exact KV records.'}
if([IO.Path]::GetFullPath($c.directory).TrimEnd('\') -eq [IO.Path]::GetPathRoot($c.directory).TrimEnd('\')){throw 'The installation directory must not be a volume root.'}
if($Plan){$c|ConvertTo-Json -Depth 12;return}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'IIS, machine certificate and service installation require an elevated setup operation.'}
$root=$c.directory;$private=Join-Path $root 'agent';$state=Join-Path $root 'state';$web=Join-Path $root ('releases/'+$c.revision)
function Write-Atomic([string]$Path,[string]$Value){[IO.File]::WriteAllText($Path+'.new',$Value);[IO.File]::Move($Path+'.new',$Path,$true)}
function Json($Value){ConvertTo-Json -InputObject $Value -Depth 12 -Compress}
Add-Type -Path "$env:windir/System32/inetsrv/Microsoft.Web.Administration.dll"
if($Render){
    $candidate=Get-Content -LiteralPath (Join-Path $private 'candidate.json') -Raw|ConvertFrom-Json
    if($candidate.runtime.server_token -notmatch '^[a-f0-9]{64}$' -or -not $candidate.runtime.engine_env){throw 'UCI runtime credential fields are missing.'}
    Write-Atomic (Join-Path $state 'server.token') $candidate.runtime.server_token
    Write-Atomic (Join-Path $state 'engine.env') $candidate.runtime.engine_env
    $pfx=Join-Path $private 'certificate.pfx'
    [IO.File]::WriteAllBytes($pfx,[Convert]::FromBase64String($candidate.certificate.pfx_base64))
    $password=ConvertTo-SecureString $candidate.certificate.password -AsPlainText -Force
    try{$certificate=Import-PfxCertificate -FilePath $pfx -Password $password -CertStoreLocation Cert:\LocalMachine\My | Where-Object HasPrivateKey | Select-Object -First 1}
    finally{$password.Dispose();Remove-Item -LiteralPath $pfx -Force}
    $names=@($certificate.DnsNameList|ForEach-Object{$_.Unicode.ToLowerInvariant()})
    $wildcard='*.'+$c.hostname.Substring($c.hostname.IndexOf('.')+1)
    if(-not $certificate -or $certificate.NotAfter -le (Get-Date) -or ($names -notcontains $c.hostname -and $names -notcontains $wildcard)){throw 'The delivered certificate does not cover this host.'}
    $manager=[Microsoft.Web.Administration.ServerManager]::new()
    try{
        $binding=$manager.Sites[$c.site].Bindings|Where-Object Protocol -EQ https|Select-Object -First 1
        if(-not $binding){throw 'The declared IIS HTTPS binding is missing.'}
        $binding.CertificateHash=$certificate.GetCertHash();$binding.CertificateStoreName='My';$binding.SslFlags=[Microsoft.Web.Administration.SslFlags]::Sni
        $manager.CommitChanges()
    }finally{$manager.Dispose()}
    Write-Atomic (Join-Path $private 'render-receipt.json') (Json @{rendered_utc=[DateTime]::UtcNow.ToString('o');source='openbao-agent';certificate=$certificate.Thumbprint})
    return
}
foreach($p in @($c.publish,$c.engine,$c.bao)){if(-not(Test-Path -LiteralPath $p)){throw "Missing installation input: $p"}}
if($c.revision -notmatch '^[a-f0-9]{7,64}$'){throw 'Declare the published connector revision.'}
foreach($p in @($root,$private,$state,$web)){[void](New-Item -ItemType Directory -Path $p -Force)}
function Protect-Directory([string]$Path,[bool]$AllowPool,[bool]$Writable){
    $acl=Get-Acl -LiteralPath $Path;$acl.SetAccessRuleProtection($true,$false)
    foreach($rule in @($acl.Access)){[void]$acl.RemoveAccessRuleSpecific($rule)}
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    if($AllowPool){$rights=if($Writable){'Modify'}else{'ReadAndExecute'};$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(('IIS APPPOOL\'+$c.site),$rights,'ContainerInherit,ObjectInherit','None','Allow'))}
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path),$acl)
}
$appcmd=Join-Path $env:windir 'System32/inetsrv/appcmd.exe'
if(-not ((& $appcmd list module /name:AspNetCoreModuleV2 2>$null)-match 'AspNetCoreModuleV2')){throw 'Install the ASP.NET Core 10 Hosting Bundle before UCI setup.'}
if((Get-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets).State -ne 'Enabled'){$null=Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -All -NoRestart}
$manager=[Microsoft.Web.Administration.ServerManager]::new()
try{
    $pool=$manager.ApplicationPools[$c.site]
    if(-not $pool){$pool=$manager.ApplicationPools.Add($c.site)}
    $pool.ManagedRuntimeVersion='';$pool.ProcessModel.IdentityType=[Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
    $pool.StartMode=[Microsoft.Web.Administration.StartMode]::AlwaysRunning
    $pool.ProcessModel.IdleTimeout=[TimeSpan]::Zero
    $pool.Recycling.PeriodicRestart.Time=[TimeSpan]::Zero
    $site=$manager.Sites[$c.site]
    if(-not $site){$site=$manager.Sites.Add($c.site,'https',('*:443:'+$c.hostname),$web);$site.ServerAutoStart=$false}
    elseif(@($site.Bindings|Where-Object BindingInformation -NE ('*:443:'+$c.hostname)).Count){throw 'Existing IIS site bindings differ from this declaration.'}
    $site.Applications['/'].ApplicationPoolName=$c.site
    $anonymous=$manager.GetApplicationHostConfiguration().GetSection('system.webServer/security/authentication/anonymousAuthentication',$c.site)
    $anonymous['enabled']=$true;$anonymous['userName']=''
    $manager.CommitChanges()
}finally{$manager.Dispose()}
Protect-Directory $root $true $false
Protect-Directory $private $false $false
Protect-Directory $state $true $false
$engineDirectory=Split-Path -Parent $c.engine
$engineAcl=Get-Acl -LiteralPath $engineDirectory
$engineAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(('IIS APPPOOL\'+$c.site),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
[IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($engineDirectory),$engineAcl)
$bao=Join-Path $private 'bao.exe'
if(-not(Test-Path $bao) -or (Get-FileHash $bao).Hash -ne (Get-FileHash $c.bao).Hash){
    $existing=Get-Service ($c.site+'-credentials') -ErrorAction SilentlyContinue
    if($existing){Stop-Service $existing.Name}
    Copy-Item -LiteralPath $c.bao -Destination $bao -Force
}
$logs=Join-Path $state 'logs';[void](New-Item -ItemType Directory -Path $logs -Force);Protect-Directory $logs $true $true
if(Test-Path (Join-Path $web 'laplace-uci-connect.exe')){
    foreach($file in Get-ChildItem $c.publish -Recurse -File | Where-Object Name -NE 'web.config'){
        $relative=[IO.Path]::GetRelativePath($c.publish,$file.FullName);$target=Join-Path $web $relative
        if(-not(Test-Path $target) -or (Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash){throw 'Published bytes differ from the installed connector revision; declare a new revision.'}
    }
}else{Copy-Item -Path (Join-Path $c.publish '*') -Destination $web -Recurse -Force}
Write-Atomic (Join-Path $root 'connector.json') (Json $c.connector)
Write-Atomic (Join-Path $root 'postgres-root.crt') $c.database_certificate
Write-Atomic (Join-Path $private 'installation.json') (Json $c)
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $private 'setup-iis.ps1') -Force
$arguments=[Security.SecurityElement]::Escape('serve --configuration "'+(Join-Path $root 'connector.json')+'"')
Write-Atomic (Join-Path $web 'web.config') ('<configuration><system.webServer><handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers><aspNetCore processPath=".\laplace-uci-connect.exe" arguments="'+$arguments+'" hostingModel="inprocess" stdoutLogEnabled="true" stdoutLogFile="'+[Security.SecurityElement]::Escape((Join-Path $logs 'iis'))+'" /></system.webServer></configuration>')
$saved=Join-Path $private 'enrollment.json'
if($Enrollment -and (Test-Path -LiteralPath $Enrollment)){$e=Get-Content -LiteralPath $Enrollment -Raw|ConvertFrom-Json;Write-Atomic $saved (Json $e)}else{$e=Get-Content -LiteralPath $saved -Raw|ConvertFrom-Json}
if($e.host -ne $c.host -or $e.address -ne $c.address){throw 'Workload enrollment does not match this host.'}
Write-Atomic (Join-Path $private 'role-id') $e.role_id;Write-Atomic (Join-Path $private 'secret-id') $e.secret_id
$contents='{"runtime":{{ with secret "'+$c.runtime_record+'" }}{{ .Data.data | toJSON }}{{ end }},"certificate":{{ with secret "'+$c.certificate_record+'" }}{{ .Data.data | toJSON }}{{ end }}}'
$agent=@"
log_file = $(Json (Join-Path $private 'openbao.log'))
log_rotate_bytes = 10485760
log_rotate_max_files = 3
vault { address = $(Json $c.address) }
auto_auth { method "approle" { config = {
role_id_file_path = $(Json (Join-Path $private 'role-id'))
secret_id_file_path = $(Json (Join-Path $private 'secret-id'))
remove_secret_id_file_after_reading = false
} } }
template_config { static_secret_render_interval = "1m"
exit_on_retry_failure = true }
template { contents = $(Json $contents)
destination = $(Json (Join-Path $private 'candidate.json'))
perms = "0600"
backup = false
error_on_missing_key = true
exec { command = $(Json @((Get-Command pwsh).Source,'-NoProfile','-File',(Join-Path $private 'setup-iis.ps1'),'-Installation',(Join-Path $private 'installation.json'),'-Render'))
timeout = "30s" }
}
"@
Write-Atomic (Join-Path $private 'agent.hcl') $agent
$service=$c.site+'-credentials';$binary='"'+$bao+'" agent -config="'+(Join-Path $private 'agent.hcl')+'"'
if(Get-Service $service -ErrorAction SilentlyContinue){
    Stop-Service $service
    $change=Get-CimInstance Win32_Service -Filter "Name='$service'" | Invoke-CimMethod -MethodName Change -Arguments @{PathName=$binary;StartMode='Automatic'}
    if($change.ReturnValue){throw "Could not update credential service: $($change.ReturnValue)"}
}
else{New-Service -Name $service -BinaryPathName $binary -StartupType Automatic | Out-Null}
& sc.exe failure $service reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if($LASTEXITCODE){throw 'Could not configure credential service recovery.'}
Remove-Item -LiteralPath (Join-Path $private 'candidate.json') -Force -ErrorAction SilentlyContinue
$began=[DateTime]::UtcNow;Start-Service $service
$ready=$false
for($i=0;$i -lt 60;$i++){
    $receipt=Join-Path $private 'render-receipt.json'
    if((Test-Path $receipt) -and ([DateTimeOffset](Get-Content $receipt -Raw|ConvertFrom-Json).rendered_utc).UtcDateTime -ge $began){$ready=$true;break}
    Start-Sleep 1
}
if(-not $ready){throw 'The workload agent did not install current credentials and certificate; inspect the service log.'}
$manager=[Microsoft.Web.Administration.ServerManager]::new()
try{$manager.Sites[$c.site].Applications['/'].VirtualDirectories['/'].PhysicalPath=$web;$manager.Sites[$c.site].ServerAutoStart=$true;$manager.CommitChanges()}finally{$manager.Dispose()}
if($c.remote_clients){
    if(Get-NetFirewallRule -Name $c.site -ErrorAction SilentlyContinue){Get-NetFirewallRule -Name $c.site | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress $c.remote_clients}
    else{New-NetFirewallRule -Name $c.site -DisplayName 'Laplace UCI private HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443 -RemoteAddress $c.remote_clients | Out-Null}
}
& $appcmd start apppool /apppool.name:$($c.site) 2>$null|Out-Null
& $appcmd start site /site.name:$($c.site) 2>$null|Out-Null
$response=Invoke-WebRequest ('https://'+$c.hostname+'/health/live') -TimeoutSec 15
if($response.StatusCode -ne 200){throw 'The declared IIS HTTPS endpoint failed its health request.'}
if($Enrollment -and (Test-Path -LiteralPath $Enrollment) -and [IO.Path]::GetFullPath($Enrollment) -ne $saved){Remove-Item -LiteralPath $Enrollment -Force}
Write-Host "Installed https://$($c.hostname) with scoped OpenBao credentials and a verified machine certificate. Run connector verify for each declared profile."
