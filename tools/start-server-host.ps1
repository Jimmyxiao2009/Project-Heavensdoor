# Forward to server tools. Prefer calling server\tools\... directly.
param([Parameter(ValueFromRemainingArguments=$true)]$Args)
& "$PSScriptRoot\..\server\tools\start-server-host.ps1" @Args
