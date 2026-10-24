#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('aot','self-contained','framework-dependent')][string]$Variant = 'aot')
$aot = $Variant -eq 'aot'
$selfContained = $Variant -ne 'framework-dependent'
$instructionSet = 'x86-64'
[pscustomobject]@{
    variant = $Variant
    nativeAot = $aot
    selfContained = $selfContained
    instructionSet = if ($aot) { $instructionSet } else { $null }
    # Scope runtime properties in the App project. Global PublishAot=true would
    # add ILCompiler to every referenced library and invalidate their lock files.
    properties = @("-p:OhMyStudyPublishVariant=$Variant") +
        $(if ($aot) { @("-p:IlcInstructionSet=$instructionSet", '-p:IlcMaxVectorTBitWidth=128') })
}
