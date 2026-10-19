param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After,
    [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
function Measure-Recognition([string]$Folder) {
    $rows = @(Get-Content -LiteralPath (Join-Path $Folder 'results.json') -Raw | ConvertFrom-Json)
    $summary = Get-Content -LiteralPath (Join-Path $Folder 'summary.json') -Raw | ConvertFrom-Json
    if ($rows.Count -ne $summary.Total) { throw 'Incomplete results' }
    $timings = @($rows.Ms | Sort-Object)
    $fields = @($rows | Where-Object { $_.Actual -and $_.Actual.Code -ceq $_.Expected.Code -and $_.Actual.Password -ceq $_.Expected.Password -and $_.Actual.App -ceq $_.Expected.App })
    $wrongAuto = @($rows | Where-Object { $_.Actual.Decision -eq 'Automatic' -and
        ($_.Expected.Decision -ne 'Automatic' -or $_.Actual.Code -cne $_.Expected.Code -or $_.Actual.Password -cne $_.Expected.Password -or $_.Actual.App -cne $_.Expected.App) })
    $visiblePwd = @($rows | Where-Object { $_.Expected.Password -ne '' })
    $targetCodes = @($rows | Where-Object { $_.Expected.Code -ne '' })
    [ordered]@{
        Total = $rows.Count; ManifestSha256 = $summary.ManifestSha256
        CodeCorrect = @($rows | Where-Object { $_.Actual -and $_.Actual.Code -ceq $_.Expected.Code }).Count
        PasswordCorrect = @($rows | Where-Object { $_.Actual -and $_.Actual.Password -ceq $_.Expected.Password }).Count
        SoftwareCorrect = @($rows | Where-Object { $_.Actual -and $_.Actual.App -ceq $_.Expected.App }).Count
        CompleteFieldsCorrect = $fields.Count; OriginalFourPartPass = @($rows | Where-Object Passed).Count
        NonemptyCodeTotal = $targetCodes.Count
        NonemptyCodeCorrect = @($targetCodes | Where-Object { $_.Actual.Code -ceq $_.Expected.Code }).Count
        VisiblePasswordTotal = $visiblePwd.Count
        VisiblePasswordCorrect = @($visiblePwd | Where-Object { $_.Actual.Password -ceq $_.Expected.Password }).Count
        WrongAuto = $wrongAuto.Count; WrongAutoImages = @($wrongAuto | ForEach-Object { $_.Expected.Image })
        WrongTargetAuto = @($rows | Where-Object { $_.Actual.Decision -eq 'Automatic' -and ($_.Actual.Code -cne $_.Expected.Code -or $_.Actual.App -cne $_.Expected.App) }).Count
        Manual = @($rows | Where-Object { $_.Actual.Decision -eq 'Manual' }).Count
        EligibleTotal = @($rows | Where-Object { $_.Expected.Decision -eq 'Automatic' }).Count
        EligibleManual = @($rows | Where-Object { $_.Expected.Decision -eq 'Automatic' -and $_.Actual.Decision -eq 'Manual' }).Count
        Errors = @($rows | Where-Object Error).Count
        AverageMs = [Math]::Round(($rows.Ms | Measure-Object -Average).Average, 2)
        P95Ms = $timings[[Math]::Ceiling(.95 * $rows.Count) - 1]
        ColdFirstImageMs = $rows[0].Ms
        WarmAverageMs = [Math]::Round((($rows | Select-Object -Skip 1).Ms | Measure-Object -Average).Average, 2)
    }
}
$beforeStats = Measure-Recognition $Before
$afterStats = Measure-Recognition $After
if ($beforeStats.ManifestSha256 -ne $afterStats.ManifestSha256 -or $beforeStats.Total -ne $afterStats.Total) { throw 'Changed dataset/answers' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
[ordered]@{ Before=$beforeStats; After=$afterStats; TimingDefinition='Sequential full-pipeline wall time; one fresh process, first image cold; p95 nearest rank; disk read included, diagnostic write excluded'; Scope='Offline decisions only; no real connection attempts; complete fields exclude decision, original four-part pass includes unchanged expected decision' } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'metrics.json') -Encoding utf8
$table = @('| 指标 | 修改前 | 修改后 |','|---|---:|---:|')
foreach ($key in @('CodeCorrect','PasswordCorrect','SoftwareCorrect','CompleteFieldsCorrect','OriginalFourPartPass','WrongAuto','WrongTargetAuto','Manual','EligibleManual','AverageMs','P95Ms')) {
    $table += "| $key | $($beforeStats[$key]) | $($afterStats[$key]) |"
}
$table | Set-Content -LiteralPath (Join-Path $Output 'metrics.md') -Encoding utf8
$beforeStats, $afterStats | ConvertTo-Json -Depth 6
