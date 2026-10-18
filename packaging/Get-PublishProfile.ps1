#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('aot','aot-avx512','self-contained','framework-dependent')][string]$Variant = 'aot')
$aot = $Variant -in @('aot','aot-avx512')
$selfContained = $Variant -ne 'framework-dependent'
# Use an explicit target, never the build agent's CPU. The baseline AOT build
# excludes optional instruction sets, including runtime-dispatched AVX-512 paths.
$instructionSet = if ($Variant -eq 'aot-avx512') { 'x86-64-v4' } else { 'x86-64,-avx512' }
[pscustomobject]@{
    variant = $Variant
    nativeAot = $aot
    selfContained = $selfContained
    instructionSet = if ($aot) { $instructionSet } else { $null }
    # Scope runtime properties in the App project. Global PublishAot=true would
    # add ILCompiler to every referenced library and invalidate their lock files.
    properties = @("-p:OhMyStudyPublishVariant=$Variant") +
        $(if ($aot) { @("-p:IlcInstructionSet=$($instructionSet.Replace(',','%2c'))", "-p:IlcMaxVectorTBitWidth=$(if ($Variant -eq 'aot-avx512') { 512 } else { 128 })") })
}
