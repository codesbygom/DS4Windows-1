[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendScript
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
# Load only the Microsoft-generated enum types; no scheduled task is queried or
# changed here. The fake Scheduler functions below shadow all mutation APIs.
Import-Module ScheduledTasks -ErrorAction Stop

$backendPath = (Resolve-Path -LiteralPath $BackendScript).Path
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $backendPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Backend installer has PowerShell parse errors: " +
        (($parseErrors | ForEach-Object Message) -join "; ")
}

function Get-BackendFunctionDefinition([string]$name) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
    }, $true)
    if (-not $definition) {
        throw "Backend installer function is missing: $name"
    }
    return $definition.Extent.Text
}

# Import the literal needed by the extracted startup functions without running
# the installer's top-level code (which would touch real machine state).
$argumentAssignments = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'script:ViiperServerArguments'
}, $true))
if ($argumentAssignments.Count -ne 1 -or
        $argumentAssignments[0].Right -isnot [Management.Automation.Language.CommandExpressionAst] -or
        $argumentAssignments[0].Right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]) {
    throw 'Backend startup arguments must have one literal definition.'
}
$script:ViiperServerArguments = $argumentAssignments[0].Right.Expression.Value
if ($script:ViiperServerArguments -ne
        'server --usb.retained-import-authority-id=4923336367393615921') {
    throw 'Backend startup arguments lost the required retained-import authority.'
}
$legacyPin = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq 'script:LegacyPortableViiperSha256'
}, $true))
if ($legacyPin.Count -ne 1 -or
        $legacyPin[0].Right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]) {
    throw 'Portable task recovery must use one fixed historical package hash.'
}
$script:LegacyPortableViiperSha256 = $legacyPin[0].Right.Expression.Value
if ($script:LegacyPortableViiperSha256 -ne
        'F1ECEF158F02D0BDCD1296C8D5097A281169081D0D59C1A8971592FAC78155EF') {
    throw 'The historical RC4.5 portable recovery identity changed.'
}

foreach ($functionName in @(
        "Assert-ManagedStartupTaskName",
        "Resolve-StartupSetupRequest",
        "Get-RootScheduledTask",
        "Convert-AccountToSid",
        "Test-TaskPrincipalEnumValue",
        "Test-HighestLogonTaskDefinition",
        "Test-HighestLogonTask",
        "Get-StartupTaskVerificationDetails",
        "Test-ManagedStartupTaskMarker",
        "Test-KnownPackagedViiperExecutable",
        "Test-LegacyManagedStartupTask",
        "Test-ManagedStartupTaskOwnership",
        "Save-ManagedStartupTaskBackup",
        "Assert-StartupTaskMutationAllowed",
        "Remove-ManagedStartupTask",
        "Remove-ManagedStartupTaskPair",
        "Add-ScheduledTaskXmlElement",
        "New-HighestLogonTaskXml",
        "Register-HighestLogonTask",
        "Register-ViiperRunTask",
        "Register-Ds4WindowsRunTask",
        "Register-ManagedStartupTaskPair",
        "Suspend-StartupTasksUntilInfrastructureReady",
        "Set-InfrastructureStartupFailClosed",
        "Set-StartupTaskWarning",
        "Enter-StartupTaskFallback",
        "Configure-StartupTasksForSetup",
        "Confirm-StartupTasksForSetup",
        "Suspend-StartupTasksForSetup",
        "Start-Ds4WindowsAfterSetup",
        "Start-ViiperAfterSetup",
        "Disable-ViiperStartup")) {
    Invoke-Expression (Get-BackendFunctionDefinition $functionName)
}

function Assert-Equal($actual, $expected, [string]$message) {
    if (-not [object]::Equals($actual, $expected)) {
        throw "$message Expected '$expected', observed '$actual'."
    }
}

function Select-TaskXmlNode([Xml.XmlDocument]$document, [string]$xpath) {
    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace("t",
        "http://schemas.microsoft.com/windows/2004/02/mit/task")
    return $document.SelectSingleNode($xpath, $namespaceManager)
}

function Select-TaskXmlNodes([Xml.XmlDocument]$document, [string]$xpath) {
    $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaceManager.AddNamespace("t",
        "http://schemas.microsoft.com/windows/2004/02/mit/task")
    return $document.SelectNodes($xpath, $namespaceManager)
}

$script:TargetUserSid =
    [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$viiperPath = "C:\Program Files\DS4Windows & Test\VIIPER\viiper.exe"
$arguments = "server --label '<&>'"
$workingDirectory = "C:\Program Files\DS4Windows & Test\VIIPER"

$taskXmlText = New-HighestLogonTaskXml $viiperPath $arguments `
    $workingDirectory
if ($taskXmlText -isnot [string]) {
    throw "Scheduled-task XML generation returned more than one object."
}
$taskXml = [Xml.XmlDocument]::new()
$taskXml.PreserveWhitespace = $true
$taskXml.LoadXml($taskXmlText)

Assert-Equal $taskXml.DocumentElement.GetAttribute("version") "1.2" `
    "The task schema version changed."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:RegistrationInfo/t:Description").InnerText `
    "DS4Windows managed startup task v1" `
    "The durable startup-task ownership marker changed."
$logonTriggers = @(Select-TaskXmlNodes $taskXml `
    "/t:Task/t:Triggers/t:LogonTrigger")
Assert-Equal $logonTriggers.Count 1 "Exactly one logon trigger is required."
Assert-Equal (@(Select-TaskXmlNodes $taskXml `
    "/t:Task/t:Triggers/t:LogonTrigger/t:UserId")).Count 0 `
    "The logon trigger must remain user-neutral."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Principals/t:Principal/t:UserId").InnerText `
    $script:TargetUserSid "The task XML did not preserve the exact SID."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Principals/t:Principal/t:LogonType").InnerText `
    "InteractiveToken" "The task is not limited to an interactive token."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Principals/t:Principal/t:RunLevel").InnerText `
    "HighestAvailable" "The task did not request highest privileges."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:MultipleInstancesPolicy").InnerText `
    "IgnoreNew" "The task multiple-instance policy changed."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:DisallowStartIfOnBatteries").InnerText `
    "false" "The task must be allowed to start on battery."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:StopIfGoingOnBatteries").InnerText `
    "false" "The task must remain running on battery."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:Enabled").InnerText "true" `
    "The registered task must be enabled by its XML definition."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:ExecutionTimeLimit").InnerText "PT0S" `
    "The startup task must not have an execution timeout."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Settings/t:Priority").InnerText "1" `
    "VIIPER startup must match the runtime High priority contract."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Actions/t:Exec/t:Command").InnerText $viiperPath `
    "The escaped executable path did not round-trip."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Actions/t:Exec/t:Arguments").InnerText $arguments `
    "The escaped arguments did not round-trip."
Assert-Equal (Select-TaskXmlNode $taskXml `
    "/t:Task/t:Actions/t:Exec/t:WorkingDirectory").InnerText `
    $workingDirectory "The escaped working directory did not round-trip."
if ($taskXmlText -notmatch '&amp;' -or $taskXmlText -notmatch '&lt;') {
    throw "Task XML values were not escaped by XmlDocument."
}

# Ask Task Scheduler to parse the definition in memory. NewTask and XmlText do
# not register, update, enable, start, or delete any system task.
$scheduleService = $null
$taskDefinition = $null
try {
    $scheduleService = New-Object -ComObject "Schedule.Service"
    $scheduleService.Connect()
    $taskDefinition = $scheduleService.NewTask(0)
    $taskDefinition.XmlText = $taskXmlText
}
finally {
    if ($taskDefinition) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $taskDefinition)
    }
    if ($scheduleService) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
            $scheduleService)
    }
}

$script:FakeTasks = @()
$script:RegisterCalls = 0
$script:RegisterNames = @()
$script:RegisterForceNames = @()
$script:RegisterFailureNames = @()
$script:StartedNames = @()
$script:DirectLaunches = @()
$script:StartFailure = $false
$script:DirectViiperSuccess = $true
$script:TaskViiperStarts = 0
$script:DirectViiperStarts = 0
$script:WarningValues = @{}
$script:RunAtStartupEnabled = $true
$script:RequestedRunAtStartupEnabled = $true
$script:FakeUserPreference = $null
$script:AlternateAdministrator = $false
$script:StartupTaskFallbackActive = $false
$script:StartupTaskWarning = ""
$script:InfrastructureRegistryPath = "HKLM:\SOFTWARE\DS4Windows"
$script:CorrelationId = "test-startup-correlation"
$script:RegistrationMutation = $null
$script:EnumerationMutation = $null
$script:RegisterRaceName = ""
$script:EnumerateCalls = 0
$script:EnumerationFailure = $false
$script:UnregisterCalls = 0
$script:DisableCalls = 0
$script:SleepCalls = 0
$script:SetupLogs = @()
$script:CapturedRegistrationXml = $null
$script:RecognizedProductPaths = @()
$script:ManagedViiperPath = $viiperPath
$script:FakeExecutableHashes = @{}
$script:FakeReparsePaths = @()
$script:TaskBackups = @{}
$script:BackupFailure = $false
$script:TaskEvents = @()

function Reset-FakeTaskState {
    $script:FakeTasks = @()
    $script:RegisterCalls = 0
    $script:RegisterNames = @()
    $script:RegisterForceNames = @()
    $script:RegisterFailureNames = @()
    $script:StartedNames = @()
    $script:DirectLaunches = @()
    $script:StartFailure = $false
    $script:DirectViiperSuccess = $true
    $script:TaskViiperStarts = 0
    $script:DirectViiperStarts = 0
    $script:WarningValues = @{}
    $script:RunAtStartupEnabled = $true
    $script:RequestedRunAtStartupEnabled = $true
    $script:FakeUserPreference = $null
    $script:AlternateAdministrator = $false
    $script:StartupTaskFallbackActive = $false
    $script:StartupTaskWarning = ""
    $script:RegistrationMutation = $null
    $script:EnumerationMutation = $null
    $script:RegisterRaceName = ""
    $script:EnumerateCalls = 0
    $script:EnumerationFailure = $false
    $script:UnregisterCalls = 0
    $script:DisableCalls = 0
    $script:SleepCalls = 0
    $script:SetupLogs = @()
    $script:CapturedRegistrationXml = $null
    $script:RecognizedProductPaths = @()
    $script:ManagedViiperPath = $viiperPath
    $script:FakeExecutableHashes = @{}
    $script:FakeReparsePaths = @()
    $script:TaskBackups = @{}
    $script:BackupFailure = $false
    $script:TaskEvents = @()
}

# Registry is deliberately not opened by this harness. The actual pure
# resolution used by the production registry adapter is exercised below.
function Update-StartupSetupRequest {
    if ($null -ne $script:FakeUserPreference) {
        $resolved = Resolve-StartupSetupRequest $script:FakeUserPreference `
            $script:RequestedRunAtStartupEnabled $script:AlternateAdministrator
        $script:RequestedRunAtStartupEnabled = $resolved.Enabled
        $script:RunAtStartupEnabled = $resolved.Enabled
    }
}

function New-FakeScheduledTask([string]$taskPath, [string]$taskName,
        [string]$definitionXml) {
    $document = [Xml.XmlDocument]::new()
    $document.LoadXml($definitionXml)
    $principalUser = (Select-TaskXmlNode $document `
        "/t:Task/t:Principals/t:Principal/t:UserId").InnerText
    $description = (Select-TaskXmlNode $document `
        "/t:Task/t:RegistrationInfo/t:Description").InnerText
    $command = (Select-TaskXmlNode $document `
        "/t:Task/t:Actions/t:Exec/t:Command").InnerText
    $argumentNode = Select-TaskXmlNode $document `
        "/t:Task/t:Actions/t:Exec/t:Arguments"
    $workingDirectoryNode = Select-TaskXmlNode $document `
        "/t:Task/t:Actions/t:Exec/t:WorkingDirectory"

    return [pscustomobject]@{
        TaskPath = $taskPath
        TaskName = $taskName
        OriginalXml = $definitionXml
        Description = $description
        Actions = @([pscustomobject]@{
            Execute = $command
            Arguments = if ($argumentNode) { $argumentNode.InnerText } else { "" }
            WorkingDirectory = if ($workingDirectoryNode) {
                $workingDirectoryNode.InnerText
            } else { "" }
        })
        Triggers = @([pscustomobject]@{
            CimClass = [pscustomobject]@{
                CimClassName = "MSFT_TaskLogonTrigger"
            }
            UserId = $null
            Enabled = $true
        })
        Principal = [pscustomobject]@{
            UserId = $principalUser
            RunLevel = "Highest"
            LogonType = "Interactive"
        }
        Settings = [pscustomobject]@{
            Enabled = $true
            Priority = if (Select-TaskXmlNode $document "/t:Task/t:Settings/t:Priority") {
                [int](Select-TaskXmlNode $document "/t:Task/t:Settings/t:Priority").InnerText
            } else { 7 }
        }
    }
}

function New-ForeignScheduledTask([string]$taskName) {
    $isViiper = [string]::Equals($taskName, "RunVIIPER",
        [StringComparison]::Ordinal)
    $xml = New-HighestLogonTaskXml `
        $(if ($isViiper) { "C:\Foreign\evil.exe" } else {
            "C:\Foreign\other.exe"
        }) $(if ($isViiper) { "not-server" } else { "not-minimized" }) `
        "C:\Foreign"
    $task = New-FakeScheduledTask "\" $taskName $xml
    $task.Description = "Unrelated owner"
    return $task
}

function Register-ScheduledTask {
    [CmdletBinding()]
    param(
        [string]$TaskPath,
        [string]$TaskName,
        [string]$Xml,
        [switch]$Force
    )
    $script:RegisterCalls++
    $script:TaskEvents += "register:$TaskName"
    $script:RegisterNames += $TaskName
    if ($Force) { $script:RegisterForceNames += $TaskName }
    $script:CapturedRegistrationXml = $Xml
    if ($script:RegisterFailureNames -contains $TaskName) {
        throw "simulated registration failure for $TaskName"
    }
    if ($TaskName -eq $script:RegisterRaceName) {
        $script:RegisterRaceName = ""
        $script:FakeTasks += New-ForeignScheduledTask $TaskName
    }
    $existing = @($script:FakeTasks | Where-Object {
        $_.TaskPath -eq $TaskPath -and $_.TaskName -eq $TaskName
    })
    if ($existing.Count -gt 0 -and -not $Force) {
        throw "simulated same-name collision"
    }
    if ($Force) {
        $script:FakeTasks = @($script:FakeTasks | Where-Object {
            -not ($_.TaskPath -eq $TaskPath -and $_.TaskName -eq $TaskName)
        })
    }
    $task = New-FakeScheduledTask $TaskPath $TaskName $Xml
    if ($script:RegistrationMutation) { & $script:RegistrationMutation $task }
    $script:FakeTasks = @($script:FakeTasks) + $task
    return $task
}

function Get-ScheduledTask {
    [CmdletBinding()]
    param()
    $script:EnumerateCalls++
    if ($script:EnumerationMutation) { & $script:EnumerationMutation $script:EnumerateCalls }
    if ($script:EnumerationFailure) {
        throw "simulated Task Scheduler enumeration failure"
    }
    return $script:FakeTasks
}

function Unregister-ScheduledTask {
    [CmdletBinding()]
    param(
        [string]$TaskPath,
        [string]$TaskName,
        [switch]$Confirm
    )
    $script:UnregisterCalls++
    $script:TaskEvents += "remove:$TaskName"
    $script:FakeTasks = @($script:FakeTasks | Where-Object {
        -not ([string]::Equals([string]$_.TaskPath, $TaskPath,
                    [StringComparison]::Ordinal) -and
            [string]::Equals([string]$_.TaskName, $TaskName,
                [StringComparison]::OrdinalIgnoreCase))
    })
}

function Enable-ScheduledTask {
    throw "Registration must not separately enable an XML-enabled task."
}

function Set-ItemProperty {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Name, [string]$Value, [string]$Type)
    Assert-Equal $LiteralPath "HKLM:\SOFTWARE\DS4Windows" "Warning persistence escaped its key."
    if ($Name -notin @("StartupTaskWarning", "StartupTaskWarningCorrelationId")) {
        throw "Unexpected registry mutation: $Name"
    }
    $script:WarningValues[$Name] = $Value
}

function Remove-ItemProperty {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Name)
    Assert-Equal $Name "VIIPER" "Unexpected startup registry cleanup."
}

function Start-ScheduledTask {
    [CmdletBinding()]
    param([string]$TaskPath, [string]$TaskName)
    $script:StartedNames += $TaskName
    if ($script:StartFailure) { throw "simulated scheduled launch denied" }
}

function Start-Process {
    [CmdletBinding()]
    param([string]$FilePath, [string]$ArgumentList, [string]$WorkingDirectory, [string]$WindowStyle)
    Assert-Equal $WindowStyle "Hidden" "Direct launch was not hidden."
    $script:DirectLaunches += $FilePath
}

function Start-AndVerifyViiper {
    param([string]$taskName, [string]$viiperPath)
    Assert-Equal $taskName "RunVIIPER" "Task launch used a different name."
    $script:TaskViiperStarts++
    return $false
}

function Start-AndVerifyViiperDirectly {
    param([string]$viiperPath)
    $script:DirectViiperStarts++
    return $script:DirectViiperSuccess
}

function New-ScheduledTaskPrincipal {
    throw "Registration must not normalize the exact SID through a CIM principal."
}

function New-ScheduledTaskTrigger {
    throw "Registration must use the schema-validated XML trigger."
}

function Test-RecognizedProductExecutable {
    param([string]$path, [string]$expectedProduct)
    return @($script:RecognizedProductPaths | Where-Object {
        [string]::Equals([string]$_, $path,
            [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
}

function Test-ManagedViiperPath {
    param([string]$path)
    return [string]::Equals($script:ManagedViiperPath, $path,
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force)
    return [pscustomobject]@{
        PSIsContainer = -not $script:FakeExecutableHashes.ContainsKey($LiteralPath)
        Attributes = if ($script:FakeReparsePaths -contains $LiteralPath) {
            [IO.FileAttributes]::ReparsePoint
        } else { [IO.FileAttributes]::Normal }
    }
}

function Test-FileSha256([string]$path, [string]$expectedHash) {
    Assert-Equal $expectedHash $script:LegacyPortableViiperSha256 `
        "Portable ownership did not request the historical package pin."
    return $script:FakeExecutableHashes.ContainsKey($path) -and
        [string]::Equals($script:FakeExecutableHashes[$path], $expectedHash,
            [StringComparison]::OrdinalIgnoreCase)
}

function Export-ScheduledTask {
    [CmdletBinding()]
    param($InputObject)
    $script:TaskEvents += "export:$($InputObject.TaskName)"
    return $InputObject.OriginalXml.Replace(
        "DS4Windows managed startup task v1", [string]$InputObject.Description)
}

function Write-StartupTaskBackup([string]$taskName, [string]$taskXml) {
    if ($script:BackupFailure) { throw "simulated backup failure" }
    $script:TaskEvents += "backup:$taskName"
    $script:TaskBackups["$taskName|$taskXml"] = $taskXml
}

function Start-Sleep {
    $script:SleepCalls++
}

function Disable-ScheduledTask {
    [CmdletBinding()]
    param(
        [string]$TaskPath,
        [string]$TaskName
    )
    $script:DisableCalls++
    $script:TaskEvents += "disable:$TaskName"
    $task = @($script:FakeTasks | Where-Object {
        $_.TaskPath -eq $TaskPath -and $_.TaskName -eq $TaskName
    }) | Select-Object -First 1
    if (-not $task) { throw "simulated task disappeared before disable" }
    $task.Settings.Enabled = $false
}

function Write-SetupLog([string]$message, $color) {
    $script:SetupLogs += $message
}

$ds4Path = "C:\Program Files\DS4Windows & Test\DS4Windows.exe"
$oldPortableDs4Path = "C:\Old Portable Copy\DS4Windows.exe"
$oldPortableDs4Directory = Split-Path -Parent $oldPortableDs4Path

function New-LegacyDs4ScheduledTask {
    $task = New-FakeScheduledTask "\" "RunDS4Windows" `
        (New-HighestLogonTaskXml $oldPortableDs4Path "-m" `
            $oldPortableDs4Directory)
    $task.Description = ""
    return $task
}

# Fresh registration uses XML without -Force, then verifies the durable owner
# marker and exact contract by enumeration.
Reset-FakeTaskState
$registered = Register-HighestLogonTask "RunVIIPER" $viiperPath `
    $arguments $workingDirectory
if (-not $registered) {
    throw "Exact-SID XML registration did not pass verification: " +
        ($script:SetupLogs -join " | ")
}
Assert-Equal $script:RegisterCalls 1 "Registration did not converge once."
Assert-Equal $script:RegisterForceNames.Count 0 `
    "Fresh registration unexpectedly used -Force."
Assert-Equal $script:UnregisterCalls 0 "Successful registration was rolled back."
Assert-Equal $script:SleepCalls 0 "Successful registration unexpectedly retried."
if (-not $script:CapturedRegistrationXml -or $script:EnumerateCalls -lt 1) {
    throw "Registration did not submit and enumerate the exact XML task."
}

$invalidNameRejected = $false
try {
    [void](Register-HighestLogonTask "RunUnexpected" $viiperPath `
        $arguments $workingDirectory)
}
catch {
    $invalidNameRejected = $_.Exception.Message -match "unmanaged"
}
if (-not $invalidNameRejected -or $script:RegisterCalls -ne 1) {
    throw "Startup-task registration accepted an unmanaged root task name."
}

# The first marker-aware repair may retarget a fully verified task created by
# an older portable copy. It upgrades that exact semantic legacy contract to
# the current path and durable marker.
Reset-FakeTaskState
$script:RecognizedProductPaths = @($oldPortableDs4Path)
$script:FakeTasks = @(New-LegacyDs4ScheduledTask)
$legacyRetargeted = Register-HighestLogonTask "RunDS4Windows" $ds4Path `
    "-m" (Split-Path -Parent $ds4Path)
if (-not $legacyRetargeted) {
    throw "Verified legacy portable RunDS4Windows task was not retargeted."
}
Assert-Equal $script:RegisterCalls 1 `
    "Legacy portable retarget did not register exactly once."
Assert-Equal $script:RegisterForceNames.Count 1 `
    "Verified legacy portable retarget did not use owned replacement."
Assert-Equal $script:UnregisterCalls 0 `
    "Legacy portable retarget used delete/recreate cleanup."
Assert-Equal $script:FakeTasks.Count 1 `
    "Legacy portable retarget left an ambiguous task set."
Assert-Equal $script:FakeTasks[0].Actions[0].Execute $ds4Path `
    "Legacy portable task did not move to the requested executable."
Assert-Equal $script:FakeTasks[0].Description `
    "DS4Windows managed startup task v1" `
    "Legacy portable task did not receive the ownership marker."

# Every semantic legacy field is conjunctive. Near-miss tasks remain foreign
# and cause zero registration, disable, or removal mutations.
$legacyNearMisses = @(
    [pscustomobject]@{
        Name = "nonblank foreign description"
        Recognized = $true
        Mutate = { param($task) $task.Description = "Another product" }
    },
    [pscustomobject]@{
        Name = "wrong arguments"
        Recognized = $true
        Mutate = { param($task) $task.Actions[0].Arguments = "--other" }
    },
    [pscustomobject]@{
        Name = "wrong working directory"
        Recognized = $true
        Mutate = { param($task) $task.Actions[0].WorkingDirectory = "C:\Other" }
    },
    [pscustomobject]@{
        Name = "wrong principal SID"
        Recognized = $true
        Mutate = { param($task) $task.Principal.UserId = "S-1-5-18" }
    },
    [pscustomobject]@{
        Name = "unrecognized product"
        Recognized = $false
        Mutate = { param($task) }
    },
    [pscustomobject]@{
        Name = "wrong trigger type"
        Recognized = $true
        Mutate = {
            param($task)
            $task.Triggers[0].CimClass.CimClassName = "MSFT_TaskTimeTrigger"
        }
    }
)
foreach ($nearMiss in $legacyNearMisses) {
    Reset-FakeTaskState
    if ($nearMiss.Recognized) {
        $script:RecognizedProductPaths = @($oldPortableDs4Path)
    }
    $candidate = New-LegacyDs4ScheduledTask
    & $nearMiss.Mutate $candidate
    $script:FakeTasks = @($candidate)
    $rejected = $false
    try {
        [void](Register-HighestLogonTask "RunDS4Windows" $ds4Path `
            "-m" (Split-Path -Parent $ds4Path))
    }
    catch { $rejected = $_.Exception.Message -match "foreign root task" }
    if (-not $rejected) {
        throw "Legacy near-miss was accepted: $($nearMiss.Name)."
    }
    Assert-Equal $script:RegisterCalls 0 `
        "Legacy near-miss reached registration: $($nearMiss.Name)."
    Assert-Equal $script:DisableCalls 0 `
        "Legacy near-miss was disabled: $($nearMiss.Name)."
    Assert-Equal $script:UnregisterCalls 0 `
        "Legacy near-miss was removed: $($nearMiss.Name)."
}

# Failure containment uses the same classifier as mutation preflight. If an
# owned moved-portable upgrade fails before -Force replaces the old task, that
# accepted legacy task is disabled; a near-miss remains untouched.
Reset-FakeTaskState
$script:RecognizedProductPaths = @($oldPortableDs4Path)
$movedLegacyForContainment = New-LegacyDs4ScheduledTask
$script:FakeTasks = @($movedLegacyForContainment)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 1 `
    "Containment did not disable an accepted moved-portable legacy task."
if ($movedLegacyForContainment.Settings.Enabled) {
    throw "Accepted moved-portable legacy task remained enabled."
}

Reset-FakeTaskState
$script:RecognizedProductPaths = @($oldPortableDs4Path)
$legacyNearMissForContainment = New-LegacyDs4ScheduledTask
$legacyNearMissForContainment.Actions[0].Arguments = "--other"
$script:FakeTasks = @($legacyNearMissForContainment)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 0 `
    "Containment disabled a legacy near-miss foreign task."
if (-not $legacyNearMissForContainment.Settings.Enabled) {
    throw "Containment mutated a legacy near-miss foreign task."
}

# Legacy VIIPER ownership is narrower still: both the requested and observed
# executable must be the canonical managed path and recognized product.
foreach ($legacyArguments in @("server", $script:ViiperServerArguments)) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($viiperPath)
    $legacyViiper = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $viiperPath $legacyArguments $workingDirectory)
    $legacyViiper.Description = ""
    $script:FakeTasks = @($legacyViiper)
    if (-not (Register-HighestLogonTask "RunVIIPER" $viiperPath `
            $script:ViiperServerArguments $workingDirectory)) {
        throw "Canonical recognized legacy VIIPER task was not migrated."
    }
    Assert-Equal $script:RegisterForceNames.Count 1 `
        "Canonical legacy VIIPER migration did not use owned replacement."
    Assert-Equal $script:FakeTasks[0].Actions[0].Arguments `
        $script:ViiperServerArguments `
        "Migrated VIIPER task did not use the current authority contract."
}

# Recover the exact shipped portable writer bug, preserving its original XML
# before replacing it with the protected executable and ownership marker.
Reset-FakeTaskState
$packagedPortablePath = "C:\Known RC4.5 Portable\viiper.exe"
$packagedPortableDirectory = Split-Path -Parent $packagedPortablePath
$script:RecognizedProductPaths = @($packagedPortablePath)
$script:FakeExecutableHashes[$packagedPortablePath] = $script:LegacyPortableViiperSha256
$packagedPortableTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $packagedPortablePath `
        $script:ViiperServerArguments $packagedPortableDirectory)
$packagedPortableTask.Description = ""
$script:FakeTasks = @($packagedPortableTask)
if (-not (Register-HighestLogonTask "RunVIIPER" $viiperPath `
        $script:ViiperServerArguments $workingDirectory)) {
    throw "Pinned packaged portable VIIPER task did not recover automatically."
}
Assert-Equal $script:FakeTasks[0].Actions[0].Execute $viiperPath `
    "Recovered portable VIIPER startup did not use the managed executable."
Assert-Equal $script:FakeTasks[0].Description `
    "DS4Windows managed startup task v1" `
    "Recovered portable VIIPER startup lost its ownership marker."
Assert-Equal $script:TaskBackups.Count 1 `
    "Portable migration did not preserve exactly one original definition."
if ($script:TaskEvents.IndexOf("backup:RunVIIPER") -ge
        $script:TaskEvents.IndexOf("register:RunVIIPER")) {
    throw "Portable migration changed startup before preserving the definition."
}
Assert-Equal $script:FakeTasks[0].Settings.Priority 1 `
    "Portable recovery did not produce the runtime High priority."

$portableNearMisses = @(
    @{ Name = "wrong hash"; Mutate = {
        param($task)
        $script:FakeExecutableHashes[$packagedPortablePath] = ('0' * 64)
    } },
    @{ Name = "file reparse point"; Mutate = {
        param($task) $script:FakeReparsePaths = @($packagedPortablePath)
    } },
    @{ Name = "ancestor reparse point"; Mutate = {
        param($task) $script:FakeReparsePaths = @($packagedPortableDirectory)
    } },
    @{ Name = "wrong arguments"; Mutate = {
        param($task) $task.Actions[0].Arguments += " --other"
    } },
    @{ Name = "wrong SID"; Mutate = {
        param($task) $task.Principal.UserId = "S-1-5-18"
    } },
    @{ Name = "foreign description"; Mutate = {
        param($task) $task.Description = "Another product"
    } },
    @{ Name = "wrong working directory"; Mutate = {
        param($task) $task.Actions[0].WorkingDirectory = "C:\Other"
    } },
    @{ Name = "extra action"; Mutate = {
        param($task) $task.Actions += $task.Actions[0]
    } },
    @{ Name = "wrong trigger"; Mutate = {
        param($task) $task.Triggers[0].CimClass.CimClassName = "MSFT_TaskTimeTrigger"
    } },
    @{ Name = "noninteractive principal"; Mutate = {
        param($task) $task.Principal.LogonType = "Password"
    } },
    @{ Name = "nonhighest principal"; Mutate = {
        param($task) $task.Principal.RunLevel = "Limited"
    } }
)
foreach ($nearMiss in $portableNearMisses) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($packagedPortablePath)
    $script:FakeExecutableHashes[$packagedPortablePath] = $script:LegacyPortableViiperSha256
    $candidate = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $packagedPortablePath `
            $script:ViiperServerArguments $packagedPortableDirectory)
    $candidate.Description = ""
    & $nearMiss.Mutate $candidate
    $script:FakeTasks = @($candidate)
    $rejected = $false
    try {
        [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
            $script:ViiperServerArguments $workingDirectory)
    }
    catch { $rejected = $_.Exception.Message -match "foreign root task" }
    if (-not $rejected) { throw "Portable near-miss accepted: $($nearMiss.Name)." }
    Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
    Assert-Equal $script:RegisterCalls 0 "Portable near-miss reached registration."
    Assert-Equal $script:DisableCalls 0 "Portable near-miss was disabled."
    Assert-Equal $script:UnregisterCalls 0 "Portable near-miss was removed."
    Assert-Equal $script:TaskBackups.Count 0 "Foreign task was archived as owned."
}

# Backup failure prevents all mutations, including failure-containment disable.
foreach ($operation in @("register", "remove", "disable")) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($packagedPortablePath)
    $script:FakeExecutableHashes[$packagedPortablePath] = $script:LegacyPortableViiperSha256
    $candidate = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $packagedPortablePath "server" $packagedPortableDirectory)
    $candidate.Description = ""
    $candidate.Settings.Priority = 7
    $script:FakeTasks = @($candidate)
    $script:BackupFailure = $true
    try {
        switch ($operation) {
            "register" {
                [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
                    $script:ViiperServerArguments $workingDirectory)
            }
            "remove" {
                [void](Remove-ManagedStartupTask "RunVIIPER" $viiperPath `
                    $script:ViiperServerArguments $workingDirectory)
            }
            "disable" { Set-InfrastructureStartupFailClosed $viiperPath $ds4Path }
        }
    }
    catch {
        if ($_.Exception.Message -notmatch "simulated backup failure") { throw }
    }
    Assert-Equal $script:RegisterCalls 0 "Backup failure allowed replacement."
    Assert-Equal $script:DisableCalls 0 "Backup failure allowed disable."
    Assert-Equal $script:UnregisterCalls 0 "Backup failure allowed removal."
    Assert-Equal $script:FakeTasks.Count 1 "Backup failure lost the previous task."
}

# A default-priority marked task is owned but not current. Repair upgrades it
# once; the next repair reuses it without another registration or backup.
Reset-FakeTaskState
$oldPriorityTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath $script:ViiperServerArguments $workingDirectory)
$oldPriorityTask.Settings.Priority = 7
$script:FakeTasks = @($oldPriorityTask)
foreach ($attempt in @(1, 2)) {
    if (-not (Register-ViiperRunTask $viiperPath "RunVIIPER")) {
        throw "High-priority startup repair did not converge."
    }
}
Assert-Equal $script:RegisterCalls 1 "High-priority repair recreated an exact task."
Assert-Equal $script:TaskBackups.Count 0 "Marked-task repair needed legacy archival."

# Recognition is an exact finite compatibility set, not a "server" prefix.
foreach ($unexpectedArguments in @(
        "server --api.addr=0.0.0.0:3242",
        ($script:ViiperServerArguments + " --other"),
        "server --usb.retained-import-authority-id=1")) {
    Reset-FakeTaskState
    $script:RecognizedProductPaths = @($viiperPath)
    $legacyArgumentNearMiss = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $viiperPath $unexpectedArguments $workingDirectory)
    $legacyArgumentNearMiss.Description = ""
    $script:FakeTasks = @($legacyArgumentNearMiss)
    $argumentNearMissRejected = $false
    try {
        [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
            $script:ViiperServerArguments $workingDirectory)
    }
    catch { $argumentNearMissRejected = $_.Exception.Message -match "foreign root task" }
    if (-not $argumentNearMissRejected) { throw "Legacy VIIPER extra arguments were accepted." }
    Assert-Equal $script:RegisterCalls 0 "Legacy argument near-miss reached registration."
    Assert-Equal $script:DisableCalls 0 "Legacy argument near-miss was disabled."
    Assert-Equal $script:UnregisterCalls 0 "Legacy argument near-miss was removed."
}

# Historical observation is allowed only for migration to the current request.
Reset-FakeTaskState
$script:RecognizedProductPaths = @($viiperPath)
$legacyDowngrade = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$legacyDowngrade.Description = ""
$script:FakeTasks = @($legacyDowngrade)
$downgradeRejected = $false
try { [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" $workingDirectory) }
catch { $downgradeRejected = $_.Exception.Message -match "foreign root task" }
if (-not $downgradeRejected) { throw "Legacy VIIPER replacement omitted the required authority." }
Assert-Equal $script:RegisterCalls 0 "Legacy downgrade reached registration."
Assert-Equal $script:DisableCalls 0 "Legacy downgrade was disabled."
Assert-Equal $script:UnregisterCalls 0 "Legacy downgrade was removed."

Reset-FakeTaskState
$foreignViiperPath = "C:\Other Portable Backend\viiper.exe"
$script:RecognizedProductPaths = @($foreignViiperPath)
$noncanonicalViiper = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $foreignViiperPath "server" `
        (Split-Path -Parent $foreignViiperPath))
$noncanonicalViiper.Description = ""
$script:FakeTasks = @($noncanonicalViiper)
$noncanonicalRejected = $false
try {
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath `
        $script:ViiperServerArguments $workingDirectory)
}
catch { $noncanonicalRejected = $_.Exception.Message -match "foreign root task" }
if (-not $noncanonicalRejected) {
    throw "Noncanonical legacy VIIPER task was accepted for migration."
}
Assert-Equal $script:RegisterCalls 0 `
    "Noncanonical legacy VIIPER collision reached registration."

# Normal absence is safe containment, not a failing exact CIM query. The
# enumeration mock accepts no TaskPath/TaskName parameters, so a regression to
# the old targeted query fails parameter binding here.
Reset-FakeTaskState
$absent = Test-HighestLogonTask "RunVIIPER" $viiperPath $arguments `
    $workingDirectory
if ($absent) { throw "An absent startup task was reported as registered." }
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 0 `
    "Failure containment attempted to mutate an absent startup task."
if (@($script:SetupLogs | Where-Object {
            $_ -match "Could not verify failure containment"
        }).Count -ne 0) {
    throw "Normal task absence was logged as a containment failure."
}

# A pre-existing foreign same-name task is a collision, never an overwrite or
# cleanup target.
Reset-FakeTaskState
$foreignViiper = New-ForeignScheduledTask "RunVIIPER"
$script:FakeTasks = @($foreignViiper)
$foreignRejected = $false
try {
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" `
        $workingDirectory)
}
catch { $foreignRejected = $_.Exception.Message -match "foreign root task" }
if (-not $foreignRejected) { throw "Foreign RunVIIPER collision was accepted." }
Assert-Equal $script:RegisterCalls 0 "Foreign collision reached registration."
Assert-Equal $script:DisableCalls 0 "Foreign collision was disabled."
Assert-Equal $script:UnregisterCalls 0 "Foreign collision was removed."
Assert-Equal $script:FakeTasks.Count 1 "Foreign collision was not preserved."

# Pair removal preflights both names, so a foreign second task cannot cause a
# partial first-task deletion when Run at Startup is disabled.
Reset-FakeTaskState
$ownedViiper = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$foreignDs4 = New-ForeignScheduledTask "RunDS4Windows"
$script:FakeTasks = @($ownedViiper, $foreignDs4)
$pairRemovalRejected = $false
try { Remove-ManagedStartupTaskPair $viiperPath $ds4Path }
catch { $pairRemovalRejected = $_.Exception.Message -match "foreign root task" }
if (-not $pairRemovalRejected) {
    throw "Foreign second-name collision did not block pair removal."
}
Assert-Equal $script:UnregisterCalls 0 `
    "Pair removal mutated its first task before validating the second."
Assert-Equal $script:FakeTasks.Count 2 `
    "Pair removal did not preserve both tasks after collision."

# Setup explicitly reclaims only the two original reserved names. The complete
# old definitions are backed up before either member of the pair is changed.
Reset-FakeTaskState
$script:FakeTasks = @((New-ForeignScheduledTask "RunVIIPER"), (New-ForeignScheduledTask "RunDS4Windows"))
if (-not (Register-ManagedStartupTaskPair $viiperPath $ds4Path)) {
    throw "The installer could not recover the two reserved task names."
}
Assert-Equal $script:RegisterCalls 2 "Reserved-name recovery did not register exactly twice."
Assert-Equal $script:RegisterForceNames.Count 2 "Existing definitions were not replaced in place."
Assert-Equal $script:TaskBackups.Count 2 "Both old definitions were not backed up."
Assert-Equal ($script:RegisterNames -join ',') "RunVIIPER,RunDS4Windows" "Setup changed task names."
foreach ($name in @("RunVIIPER", "RunDS4Windows")) {
    $backupIndex = [Array]::IndexOf([string[]]$script:TaskEvents, "backup:$name")
    $firstRegistration = [Array]::IndexOf([string[]]$script:TaskEvents, "register:RunVIIPER")
    if ($backupIndex -lt 0 -or $backupIndex -gt $firstRegistration) {
        throw "The installer mutated tasks before backing up the complete pair."
    }
}
Assert-Equal $script:UnregisterCalls 0 `
    "Reserved-name recovery used delete/recreate instead of replacement."

# A genuine second-task provider failure rolls back only RunVIIPER created by
# this pair transaction; no unowned task is touched.
Reset-FakeTaskState
$script:RegisterFailureNames = @("RunDS4Windows")
$partialFailureObserved = $false
try { [void](Register-ManagedStartupTaskPair $viiperPath $ds4Path) }
catch {
    $partialFailureObserved = $_.Exception.Message -match `
        "Could not register the elevated RunDS4Windows"
}
if (-not $partialFailureObserved) {
    throw "Simulated second-task registration failure was not propagated."
}
Assert-Equal $script:UnregisterCalls 1 `
    "Pair rollback did not remove exactly its newly created RunVIIPER task."
Assert-Equal $script:FakeTasks.Count 0 `
    "Pair rollback left a partial startup-task set."

# Failure containment may disable a marker-owned task even when a failed
# update left its action malformed. The marker establishes ownership; a
# foreign same-name task remains untouched.
Reset-FakeTaskState
$markerTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath "server" $workingDirectory)
$markerTask.Actions[0].Execute = "C:\Broken\unexpected.exe"
$script:FakeTasks = @($markerTask)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 1 `
    "Containment did not disable a marker-owned malformed task."
if ($markerTask.Settings.Enabled) {
    throw "Marker-owned malformed task remained enabled after containment."
}

Reset-FakeTaskState
$script:FakeTasks = @(
    (New-ForeignScheduledTask "RunVIIPER"),
    (New-ForeignScheduledTask "RunDS4Windows")
)
Set-InfrastructureStartupFailClosed $viiperPath $ds4Path
Assert-Equal $script:DisableCalls 0 `
    "Containment disabled a foreign same-name task."
Assert-Equal $script:UnregisterCalls 0 `
    "Containment removed a foreign same-name task."

# Enumeration/service failures remain real failures and must never be
# translated into normal absence or followed by a mutation.
Reset-FakeTaskState
$script:EnumerationFailure = $true
$enumerationFailureObserved = $false
try {
    [void](Register-HighestLogonTask "RunVIIPER" $viiperPath "server" `
        $workingDirectory)
}
catch {
    $enumerationFailureObserved = $_.Exception.Message -match `
        "enumeration failure"
}
if (-not $enumerationFailureObserved) {
    throw "Task Scheduler enumeration failure was masked as task absence."
}
Assert-Equal $script:RegisterCalls 0 `
    "Registration continued after enumeration failed."
Assert-Equal $script:DisableCalls 0 `
    "Enumeration failure triggered a disable mutation."
Assert-Equal $script:UnregisterCalls 0 `
    "Enumeration failure triggered a removal mutation."

# The real pre-install sequence must leave a readable foreign reserved task
# for explicit backup/reclaim, rather than bypassing Configure as a failure.
Reset-FakeTaskState
$script:InstallDir = $workingDirectory
$script:Ds4WindowsRestartPath = $ds4Path
$script:TargetRunKeyPath = "HKCU:\Fixture\Run"
$script:FakeTasks = @((New-ForeignScheduledTask "RunVIIPER"), (New-ForeignScheduledTask "RunDS4Windows"))
Disable-ViiperStartup
Assert-Equal $script:StartupTaskFallbackActive $false "Strict pre-cleanup prevented authorized setup recovery."
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $false "Reserved-name recovery unnecessarily entered direct mode."
Assert-Equal $script:TaskBackups.Count 2 "Pre-cleanup lost a reserved task's original definition."

# A failed archive never authorizes replacement. Setup remains usable in
# direct mode and publishes a warning for this invocation only.
Reset-FakeTaskState
$originalViiper = New-ForeignScheduledTask "RunVIIPER"
$script:FakeTasks = @($originalViiper)
$script:BackupFailure = $true
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $true "Backup failure aborted instead of selecting direct mode."
Assert-Equal $script:RegisterCalls 0 "Backup failure permitted a registration mutation."
Assert-Equal $script:DisableCalls 0 "Backup failure disabled an unowned task."
Assert-Equal (Get-RootScheduledTask "RunVIIPER") $originalViiper "Backup failure changed the original task."
Assert-Equal $script:WarningValues.StartupTaskWarningCorrelationId $script:CorrelationId `
    "Startup warning was not correlated to this setup invocation."
if ([string]::IsNullOrWhiteSpace($script:WarningValues.StartupTaskWarning)) { throw "Startup warning was missing." }
Confirm-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal (Start-ViiperAfterSetup $viiperPath $ds4Path) $true "Direct VIIPER launch did not remain available."
Start-Ds4WindowsAfterSetup $viiperPath $ds4Path
Assert-Equal $script:TaskViiperStarts 0 "Direct mode attempted a VIIPER scheduled launch."
Assert-Equal $script:StartedNames.Count 0 "Direct mode attempted a DS4 scheduled launch."
Assert-Equal $script:DirectViiperStarts 1 "Direct mode did not launch VIIPER once."
Assert-Equal $script:DirectLaunches.Count 1 "Direct mode did not launch DS4 once."

# Burn Retry may retain the correlation ID. A successful retry clears BOTH
# old warning values and restores this invocation's requested startup policy.
$script:BackupFailure = $false
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $false "Successful retry remained in direct mode."
Assert-Equal $script:RunAtStartupEnabled $true "Successful retry lost the requested startup preference."
Assert-Equal $script:WarningValues.StartupTaskWarning "" "Successful retry retained stale warning text."
Assert-Equal $script:WarningValues.StartupTaskWarningCorrelationId "" "Successful retry retained stale warning correlation."

# Already exact tasks need no mutation or archive. An unavailable backup
# destination must not disable otherwise healthy automatic startup.
$script:BackupFailure = $true
$registrationsBefore = $script:RegisterCalls
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $false "An exact task unnecessarily required a backup."
Assert-Equal $script:RegisterCalls $registrationsBefore "An exact task was unnecessarily rewritten."

# Changed marked definitions DO require backup before repair.
Reset-FakeTaskState
$markedTask = New-FakeScheduledTask "\" "RunVIIPER" `
    ((New-HighestLogonTaskXml $viiperPath $script:ViiperServerArguments $workingDirectory).Replace('<Priority>1</Priority>', '<Priority>7</Priority>'))
$script:FakeTasks = @($markedTask)
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:TaskBackups.Count 1 "A changed marked task was replaced without a backup."
Assert-Equal (Get-RootScheduledTask "RunVIIPER").Settings.Priority 1 "Marked task repair failed."

Reset-FakeTaskState
$markedTask = New-FakeScheduledTask "\" "RunVIIPER" `
    ((New-HighestLogonTaskXml $viiperPath $script:ViiperServerArguments $workingDirectory).Replace('<Priority>1</Priority>', '<Priority>7</Priority>'))
$script:FakeTasks = @($markedTask)
$script:BackupFailure = $true
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:RegisterCalls 0 "Failed marked-task backup permitted replacement."
Assert-Equal $script:DisableCalls 0 "Failed marked-task backup indirectly disabled the original."
Assert-Equal $markedTask.Settings.Enabled $true "Failed marked-task backup changed the original definition."

# The definition can change after pair preflight but before registration.
# The immediate pre-write reread must archive that updated definition too.
Reset-FakeTaskState
$script:FakeTasks = @((New-ForeignScheduledTask "RunVIIPER"), (New-ForeignScheduledTask "RunDS4Windows"))
$script:EnumerationMutation = {
    param($count)
    if ($count -eq 4) { $script:FakeTasks[0].Description = "Changed immediately before overwrite" }
}
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:TaskBackups.Count 3 "A changed pre-write definition was not separately backed up."
if (-not ($script:TaskBackups.Values -join ' ').Contains('Changed immediately before overwrite')) {
    throw "The latest task definition was not archived."
}

# A racing creation first rejects create-only registration. The bounded retry
# may replace it only after a fresh archive; no extra task name is invented.
Reset-FakeTaskState
$script:RegisterRaceName = "RunVIIPER"
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $false "A racing reserved task could not recover safely."
Assert-Equal $script:RegisterCalls 3 "A racing task escaped bounded registration attempts."
Assert-Equal $script:TaskBackups.Count 1 "A racing task was overwritten without backup."
Assert-Equal ($script:RegisterForceNames -join ',') "RunVIIPER" "A racing task replacement targeted the wrong name."

foreach ($failure in @("provider unavailable", "registration denied", "startup-disabled cleanup")) {
    Reset-FakeTaskState
    if ($failure -eq "provider unavailable") { $script:EnumerationFailure = $true }
    if ($failure -eq "registration denied") { $script:RegisterFailureNames = @("RunDS4Windows") }
    if ($failure -eq "startup-disabled cleanup") {
        $script:RequestedRunAtStartupEnabled = $false
        $script:FakeTasks = @(New-ForeignScheduledTask "RunVIIPER")
    }
    Configure-StartupTasksForSetup $viiperPath $ds4Path
    Assert-Equal $script:StartupTaskFallbackActive $true "Task failure did not degrade to direct launch: $failure"
    Assert-Equal $script:RunAtStartupEnabled $false "Task failure retained scheduled launch: $failure"
    Confirm-StartupTasksForSetup $viiperPath $ds4Path
    $script:DirectViiperSuccess = $false
    Assert-Equal (Start-ViiperAfterSetup $viiperPath $ds4Path) $false `
        "A failed direct runtime launch was incorrectly reported ready."
}

# Issue #98: verification remains strict, and reports which fields differ.
# Native CIM integer values and Microsoft's generated adapter enums are the
# same semantics. No truthy/coercible alternatives count as elevated interactive.
Reset-FakeTaskState
$runLevelType = 'Microsoft.PowerShell.Cmdletization.GeneratedTypes.ScheduledTask.RunLevelEnum' -as [type]
$logonType = 'Microsoft.PowerShell.Cmdletization.GeneratedTypes.ScheduledTask.LogonTypeEnum' -as [type]
if (-not $runLevelType -or -not $logonType) { throw "Microsoft Scheduler enum types were unavailable." }
foreach ($representation in @(
        @("Highest", "Interactive"),
        @([int]1, [int]3),
        @([byte]1, [long]3),
        @([Enum]::ToObject($runLevelType, 1), [Enum]::ToObject($logonType, 3)))) {
    $task = New-FakeScheduledTask "\" "RunVIIPER" `
        (New-HighestLogonTaskXml $viiperPath $script:ViiperServerArguments $workingDirectory)
    $task.Principal.RunLevel = $representation[0]
    $task.Principal.LogonType = $representation[1]
    Assert-Equal (Test-HighestLogonTaskDefinition $task $viiperPath `
        $script:ViiperServerArguments $workingDirectory) $true "A documented Scheduler representation was rejected."
}
foreach ($kind in @("RunLevel", "LogonType")) {
    $expected = if ($kind -eq "RunLevel") { 1 } else { 3 }
    foreach ($invalid in @($null, $true, $false, [double]$expected, [decimal]$expected,
            [string]$expected, "Unknown", 0, 6, [char]$expected, [DayOfWeek]$expected, @($expected))) {
        Assert-Equal (Test-TaskPrincipalEnumValue $invalid $kind) $false `
            "A non-contract Scheduler representation was accepted for $kind."
    }
}
$disabledTriggerTask = New-FakeScheduledTask "\" "RunVIIPER" `
    (New-HighestLogonTaskXml $viiperPath $script:ViiperServerArguments $workingDirectory)
$disabledTriggerTask.Triggers[0].Enabled = $false
Assert-Equal (Test-HighestLogonTaskDefinition $disabledTriggerTask $viiperPath `
    $script:ViiperServerArguments $workingDirectory) $false "A disabled logon trigger was accepted as ready."
Assert-Equal (Test-HighestLogonTaskDefinition $disabledTriggerTask $viiperPath `
    $script:ViiperServerArguments $workingDirectory $false) $true "Disabled ownership/suspension validation changed."

# Issue #98: verification remains strict, and reports which fields differ.
# Do not log raw task arguments, which may contain credentials or other data.
Reset-FakeTaskState
$script:RegistrationMutation = { param($task) $task.Settings.Priority = 7 }
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $true "Invalid priority passed exact verification."
$allLogs = $script:SetupLogs -join ' '
foreach ($field in @('"priority":7', '"principalSid":', '"triggerType":', '"argumentsMatch":', '"workingDirectoryMatches":')) {
    if (-not $allLogs.Contains($field)) { throw "Verification diagnostics lost field: $field" }
}
Reset-FakeTaskState
Configure-StartupTasksForSetup $viiperPath $ds4Path
$script:FakeTasks[0].Actions[0].Arguments = "private-token-should-not-be-logged"
Confirm-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:StartupTaskFallbackActive $true "Changed task verification aborted instead of warning."
if (($script:SetupLogs -join ' ').Contains('private-token-should-not-be-logged')) { throw "Task diagnostics leaked raw arguments." }

# A task-only suspension failure must not clear the existing driver reboot
# boundary or claim that startup suspension succeeded.
Reset-FakeTaskState
Configure-StartupTasksForSetup $viiperPath $ds4Path
$script:FakeTasks[0].Actions[0].Arguments = "unexpected"
$script:UsbipRuntimeReady = $false
$script:RebootRecommended = $true
$script:ExitCode = 3010
Assert-Equal (Suspend-StartupTasksForSetup $viiperPath $ds4Path) $false "A failed suspension claimed success."
Assert-Equal $script:StartupTaskFallbackActive $true "Failed suspension did not record a warning."
Assert-Equal $script:UsbipRuntimeReady $false "Task failure bypassed the driver readiness gate."
Assert-Equal $script:RebootRecommended $true "Task failure cleared the driver reboot requirement."
Assert-Equal $script:ExitCode 3010 "Task failure changed the pending-reboot result."

Reset-FakeTaskState
Configure-StartupTasksForSetup $viiperPath $ds4Path
$script:StartFailure = $true
Start-Ds4WindowsAfterSetup $viiperPath $ds4Path
Assert-Equal $script:StartedNames.Count 1 "DS4 scheduled launch was not attempted once."
Assert-Equal $script:DirectLaunches.Count 1 "DS4 task launch failure did not fall back directly."
Assert-Equal $script:StartupTaskFallbackActive $true "DS4 task launch failure did not warn."

# Original task names only; runtime/cleanup ownership remains strict. Check
# top-level wiring without running the installer or touching live drivers.
$backendText = $ast.Extent.Text

# Execute the production alternate-administrator guard with in-memory identity
# data only. Configure and Retry must not restore the initially requested true
# preference after this invocation deferred another account's logon tasks.
Reset-FakeTaskState
$script:TargetUserName = "Fixture\TargetUser"
$elevatedIdentity = [pscustomobject]@{
    User = [pscustomobject]@{ Value = "S-1-5-18" }
}
$alternateAdminGuard = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains('$elevatedIdentity.User.Value') -and
        $node.Extent.Text.Contains('alternate administrator credentials')
}, $true)
if (-not $alternateAdminGuard) { throw "The alternate-administrator policy guard is missing." }
Invoke-Expression $alternateAdminGuard.Extent.Text
Assert-Equal $script:RunAtStartupEnabled $false "Alternate-admin startup was not deferred."
Assert-Equal $script:RequestedRunAtStartupEnabled $false "Configure could undo alternate-admin deferral."
Configure-StartupTasksForSetup $viiperPath $ds4Path
Configure-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:RegisterCalls 0 "Configure/Retry registered startup under alternate administrator credentials."
Assert-Equal $script:RunAtStartupEnabled $false "Configure/Retry re-enabled deferred alternate-admin startup."
Assert-Equal $script:StartupTaskFallbackActive $false "Intentional alternate-admin deferral was treated as a provider failure."

# A restart does not turn an installer default into an opt-out. Explicit
# choices made while setup was paused remain authoritative at confirmation.
foreach ($case in @(
        @($null, $true, $true, $true, $false),
        @(0, $true, $false, $false, $false),
        @(1, $false, $false, $true, $true),
        @($null, $true, $false, $true, $true))) {
    $resolved = Resolve-StartupSetupRequest $case[0] $case[1] $case[2]
    Assert-Equal $resolved.Requested $case[3] "Requested startup intent changed."
    Assert-Equal $resolved.Enabled $case[4] "Task authorization did not follow the account boundary."
}
Reset-FakeTaskState
Configure-StartupTasksForSetup $viiperPath $ds4Path
$script:FakeUserPreference = 0
Confirm-StartupTasksForSetup $viiperPath $ds4Path
Assert-Equal $script:FakeTasks.Count 0 "An opt-out during registration was lost."
Assert-Equal $script:RunAtStartupEnabled $false "Canceled startup remained selected for automatic launch."

# Execute the actual branch containing registration with fake Scheduler and
# driver owners. Fresh setup must not expose an enabled task before the ABI
# gate; absence of tasks is a valid suspended state on a fresh installation.
$registrationGate = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -eq '$script:UsbipRuntimeReady -and -not $script:RebootRecommended' -and
        $node.Extent.Text.Contains('Configure-StartupTasksForSetup $viiperPath')
}, $true)
if (-not $registrationGate) { throw "Task registration is not inside the runtime ABI gate." }
function Stop-ViiperProcesses { return $true }
foreach ($ready in @($false, $true)) {
    Reset-FakeTaskState
    $script:UsbipRuntimeReady = $ready
    $script:RebootRecommended = -not $ready
    $script:Ds4WindowsRestartPath = $ds4Path
    Invoke-Expression $registrationGate.Extent.Text
    Assert-Equal $script:RegisterCalls $(if ($ready) { 2 } else { 0 }) "ABI gate registered tasks at the wrong time."
}

if ($backendText.Contains('DS4Windows.RunVIIPER') -or $backendText.Contains('DS4Windows.RunDS4Windows')) {
    throw "Setup introduced alternate task names."
}
$outerFailure = $backendText.Substring($backendText.LastIndexOf('catch {', $backendText.IndexOf('if ($script:UserCanceled)')))
if (-not $outerFailure.Contains('if ($script:InstallDir -and')) {
    throw "Real infrastructure failure lost task containment after startup fallback."
}
if ($outerFailure.Contains('if ($script:RunAtStartupEnabled -and $script:InstallDir')) {
    throw "Startup fallback suppresses later infrastructure failure containment."
}
foreach ($required in @('Configure-StartupTasksForSetup $viiperPath',
        'Confirm-StartupTasksForSetup $viiperPath',
        'Start-ViiperAfterSetup $viiperPath',
        'Start-Ds4WindowsAfterSetup $viiperPath',
        'if ($script:UsbipRuntimeReady -and -not $script:RebootRecommended)',
        'throw "VIIPER installed, but its local API did not start.')) {
    if (-not $backendText.Contains($required)) { throw "Setup readiness wiring lost: $required" }
}

Write-Host (
    "Exact-SID startup-task XML, schema, ownership, collision, rollback, " +
    "containment, and absence simulations passed without changing Task " +
    "Scheduler state."
)
