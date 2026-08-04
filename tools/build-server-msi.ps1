param([Parameter(ValueFromRemainingArguments=$true)]$Args)
& "$PSScriptRoot\..\server\tools\build-server-msi.ps1" @Args
