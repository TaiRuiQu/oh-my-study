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
    properties = @("-p:PublishAot=$($aot.ToString().ToLowerInvariant())",
        "-p:SelfContained=$($selfContained.ToString().ToLowerInvariant())",
        '-p:PublishReadyToRun=false', "-p:PublishTrimmed=$($aot.ToString().ToLowerInvariant())", '-p:UseAppHost=true', "-p:OhMyStudyPublishVariant=$Variant") +
        $(if ($aot) { @("-p:IlcInstructionSet=$($instructionSet.Replace(',','%2c'))", "-p:IlcMaxVectorTBitWidth=$(if ($Variant -eq 'aot-avx512') { 512 } else { 128 })") })
}
