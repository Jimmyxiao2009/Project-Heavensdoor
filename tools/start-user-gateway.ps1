param([Parameter(ValueFromRemainingArguments=$true)]$Args)
& "$PSScriptRoot\..\server\tools\start-user-gateway.ps1" @Args
