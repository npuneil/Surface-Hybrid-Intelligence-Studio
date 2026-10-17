#Requires -Version 7.2
Set-StrictMode -Version Latest

# Edition harness. A tree's edition.json is its brand manifest. Two kinds of fields:
#  - Identity tokens (codeName, AUMID, service app id, ...) are distinctive strings that appear in code,
#    paths and docs. They change only through tools\New-Edition.ps1, which maps source -> target in one pass.
#  - Presentation fields (mark, logo, accent colours) are applied by key through tools\Sync-Edition.ps1,
#    which regenerates native\Brand.cs, the app icon/logo and the XAML/CSS accent values.

$script:IdentityFields = @('displayName', 'codeName', 'appUserModelId', 'serviceAppId', 'schemaPrefix', 'envPrefix', 'shortcutName', 'repository')
$script:AccentKeys = [ordered]@{ base = ''; light1 = 'Light1'; light2 = 'Light2'; light3 = 'Light3'; dark1 = 'Dark1'; dark2 = 'Dark2'; dark3 = 'Dark3'; tint = $null }
$script:WebAccentVariables = [ordered]@{ base = '--accent'; deep = '--accent-deep'; focus = '--focus' }
$script:TextExtensions = @('.cs', '.xaml', '.csproj', '.manifest', '.config', '.py', '.ps1', '.psm1', '.js', '.html', '.css', '.md', '.json', '.svg', '.txt', '.yml', '.yaml')
$script:TextNames = @('.gitignore', '.gitattributes', '.editorconfig')
$script:GeneratedFiles = @('edition.json', 'native\Brand.cs', 'native\Assets\AppIcon.ico', 'native\Assets\AppLogo.png', 'native\Assets\AppLogo.svg')

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Read-Edition {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [switch]$Target)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Edition manifest not found: $Path" }
    $edition = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    Test-EditionManifest -Edition $edition -Target:$Target -Source $Path
    return $edition
}

function Test-EditionManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Edition, [switch]$Target, [string]$Source = 'edition')
    $errors = [System.Collections.Generic.List[string]]::new()
    $text = [ordered]@{
        id = '^[a-z][a-z0-9-]{1,30}$'
        displayName = '^[A-Za-z0-9][A-Za-z0-9 &\-]{5,60}$'
        device = '^[^\r\n"]{3,60}$'
        mark = '^[^\r\n"]{1,24}$'
        markSubtitle = '^[^\r\n"]{1,40}$'
        codeName = '^[A-Z][A-Za-z0-9]{5,40}$'
        appUserModelId = '^[A-Za-z0-9]+(\.[A-Za-z0-9]+){1,3}$'
        serviceAppId = '^[a-z0-9]+(-[a-z0-9]+)+$'
        schemaPrefix = '^[a-z0-9]+(-[a-z0-9]+)+$'
        envPrefix = '^[A-Z][A-Z0-9]*_([A-Z0-9]+_)*$'
        shortcutName = '^[^\\/:*?"<>|\r\n]{6,80}$'
        repository = '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$'
    }
    if ((Get-Field $Edition 'schema') -ne 'studio-edition/1') { $errors.Add("schema must be 'studio-edition/1'") }
    foreach ($name in $text.Keys) {
        $value = Get-Field $Edition $name
        if ($value -isnot [string] -or $value -cnotmatch $text[$name]) { $errors.Add("$name must match $($text[$name]) (got '$value')") }
    }
    if ((Get-Field $Edition 'visibility') -cnotin @('public', 'private')) { $errors.Add('visibility must be public or private') }
    $hex = '^#[0-9A-Fa-f]{6}$'
    $logo = Get-Field $Edition 'logo'
    if ((Get-Field $logo 'text') -cnotmatch '^[A-Za-z0-9]{1,4}$') { $errors.Add('logo.text must be 1-4 letters or digits') }
    foreach ($key in 'background', 'foreground') { if ((Get-Field $logo $key) -notmatch $hex) { $errors.Add("logo.$key must be #RRGGBB") } }
    $accent = Get-Field $Edition 'accent'
    foreach ($key in $script:AccentKeys.Keys) { if ((Get-Field $accent $key) -notmatch $hex) { $errors.Add("accent.$key must be #RRGGBB") } }
    $web = Get-Field $Edition 'webAccent'
    foreach ($key in $script:WebAccentVariables.Keys) { if ((Get-Field $web $key) -notmatch $hex) { $errors.Add("webAccent.$key must be #RRGGBB") } }
    $targetOnly = @('rules', 'forbiddenTokens') | Where-Object { $null -ne $Edition.PSObject.Properties[$_] }
    if ($targetOnly -and -not $Target) { $errors.Add("$($targetOnly -join ', ') belong only in editions\<id>.json targets, not in a tree's edition.json") }
    foreach ($rule in @(Get-Field $Edition 'rules')) {
        if ($null -eq $rule) { continue }
        $from = Get-Field $rule 'from'; $to = Get-Field $rule 'to'
        if ($from -isnot [string] -or $from.Length -lt 3 -or $to -isnot [string]) { $errors.Add("each rule needs 'from' (3+ characters) and 'to' strings") }
        foreach ($pattern in @(Get-Field $rule 'files')) { if ($null -ne $pattern -and $pattern -isnot [string]) { $errors.Add("rule 'files' must be path wildcards") } }
    }
    foreach ($token in @(Get-Field $Edition 'forbiddenTokens')) {
        if ($null -ne $token -and ($token -isnot [string] -or $token.Length -lt 2)) { $errors.Add('forbiddenTokens must be strings of 2+ characters') }
    }
    if ($errors.Count) { throw "Invalid edition manifest ($Source):`n - " + ($errors -join "`n - ") }
}

function ConvertTo-TreeManifest {
    <# The manifest written into a generated tree: target fields without rules/forbidden tokens (they name the source brand). #>
    param([Parameter(Mandatory)]$Edition)
    $copy = [ordered]@{}
    foreach ($property in $Edition.PSObject.Properties) {
        if ($property.Name -notin @('rules', 'forbiddenTokens')) { $copy[$property.Name] = $property.Value }
    }
    return [pscustomobject]$copy
}

function Get-EditionIdentityMap {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$From, [Parameter(Mandatory)]$To)
    $pairs = [System.Collections.Generic.List[object]]::new()
    foreach ($field in $script:IdentityFields) { $pairs.Add([pscustomobject]@{ Field = $field; From = $From.$field; To = $To.$field }) }
    $pairs.Add([pscustomobject]@{ Field = 'repository name'; From = $From.repository.Split('/')[1]; To = $To.repository.Split('/')[1] })
    # Matching is ordinal, so tokens differing only by case are distinct identities.
    $seen = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $unique = [System.Collections.Generic.List[object]]::new()
    foreach ($pair in $pairs) {
        if ([string]::IsNullOrEmpty($pair.From)) { throw "Identity field '$($pair.Field)' is empty in the source edition." }
        if ($seen.ContainsKey($pair.From)) {
            if ($seen[$pair.From] -cne $pair.To) { throw "Identity conflict: '$($pair.From)' maps to both '$($seen[$pair.From])' and '$($pair.To)'" }
            continue
        }
        $seen[$pair.From] = $pair.To
        if ($pair.From -cne $pair.To) { $unique.Add($pair) }
    }
    return @($unique | Sort-Object -Property @{ Expression = { $_.From.Length }; Descending = $true }, @{ Expression = { $_.From }; Descending = $false } -CaseSensitive)
}

function Convert-EditionText {
    <# Single pass, longest match first, ordinal case-sensitive: replacements never re-match their own output. #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [object[]]$Pairs, [hashtable]$Counts)
    if (-not $Pairs -or -not $Text) { return $Text }
    $ordered = @($Pairs | Sort-Object -Property @{ Expression = { $_.From.Length }; Descending = $true })
    $lookup = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($pair in $ordered) { if (-not $lookup.ContainsKey($pair.From)) { $lookup[$pair.From] = $pair.To } }
    $pattern = ($ordered | ForEach-Object { [regex]::Escape($_.From) }) -join '|'
    $tally = if ($null -ne $Counts) { $Counts } else { @{} }
    $evaluator = { param($m) $tally[$m.Value] = [int]$tally[$m.Value] + 1; $lookup[$m.Value] }.GetNewClosure()
    return [regex]::Replace($Text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]$evaluator)
}

function Get-EditionRules {
    <# Rules that apply to one relative path (rule.files are -like wildcards; none = every text file). #>
    param($Edition, [Parameter(Mandatory)][string]$RelativePath)
    $path = $RelativePath.Replace('/', '\')
    return @(@(Get-Field $Edition 'rules') | Where-Object {
        if ($null -eq $_) { return $false }
        $files = @(Get-Field $_ 'files') | Where-Object { $_ }
        -not $files -or @($files | Where-Object { $path -like $_.Replace('/', '\') }).Count -gt 0
    } | ForEach-Object { [pscustomobject]@{ From = $_.from; To = $_.to } })
}

function ConvertTo-CSharpString([string]$Value) { '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"' }

function Get-BrandSource {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Edition)
    $constants = [ordered]@{
        EditionId = $Edition.id
        DisplayName = $Edition.displayName
        Device = $Edition.device
        WindowTitle = "$($Edition.displayName) - $($Edition.device)"
        Mark = $Edition.mark
        MarkSubtitle = $Edition.markSubtitle
        CodeName = $Edition.codeName
        AppUserModelId = $Edition.appUserModelId
        ServiceAppId = $Edition.serviceAppId
        SchemaPrefix = $Edition.schemaPrefix
        EnvPrefix = $Edition.envPrefix
        Accent = $Edition.accent.base.ToUpperInvariant()
        AccentDeep = $Edition.accent.dark2.ToUpperInvariant()
        AccentTint = $Edition.accent.tint.ToUpperInvariant()
    }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]@(
        '// <auto-generated>'
        '// Generated from edition.json by tools\Sync-Edition.ps1. Edit edition.json and rerun that script.'
        '// </auto-generated>'
        "namespace $($Edition.codeName);"
        ''
        '/// <summary>Edition identity and accent. The self-test checks these against edition.json.</summary>'
        'internal static class Brand'
        '{'))
    foreach ($name in $constants.Keys) { $lines.Add("    internal const string $name = $(ConvertTo-CSharpString $constants[$name]);") }
    $lines.Add('}')
    return ($lines -join "`r`n") + "`r`n"
}

function Get-LogoSvg {
    param([Parameter(Mandatory)]$Edition)
    $label = [System.Security.SecurityElement]::Escape("$($Edition.displayName) logo")
    $text = [System.Security.SecurityElement]::Escape($Edition.logo.text)
    $size = @{ 1 = 150; 2 = 120; 3 = 87; 4 = 68 }[$Edition.logo.text.Length]
    $baseline = 128 + [int]($size * 0.29)
    return (@(
        "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 256 256"" role=""img"" aria-label=""$label"">"
        "  <rect x=""9"" y=""9"" width=""238"" height=""238"" rx=""45"" fill=""$($Edition.logo.background)""/>"
        "  <text x=""128"" y=""$baseline"" text-anchor=""middle"" font-family=""Segoe UI, sans-serif"" font-weight=""700"" font-size=""$size"" fill=""$($Edition.logo.foreground)"">$text</text>"
        '</svg>'
    ) -join "`r`n") + "`r`n"
}

function Set-AccentInXaml {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)]$Edition)
    foreach ($key in $script:AccentKeys.Keys) {
        $suffix = $script:AccentKeys[$key]
        if ($null -eq $suffix) { continue }
        $pattern = '(<Color x:Key="SystemAccentColor' + $suffix + '">)#[0-9A-Fa-f]{6}(</Color>)'
        if ($Text -notmatch $pattern) { throw "App.xaml is missing SystemAccentColor$suffix" }
        $value = $Edition.accent.$key.ToUpperInvariant()
        $Text = [regex]::Replace($Text, $pattern, '${1}' + $value + '${2}')
    }
    return $Text
}

function Set-AccentInCss {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)]$Edition)
    foreach ($key in $script:WebAccentVariables.Keys) {
        $pattern = '(' + [regex]::Escape($script:WebAccentVariables[$key]) + ':)#[0-9A-Fa-f]{6}(?=[;}])'
        if ($Text -notmatch $pattern) { throw "style.css is missing $($script:WebAccentVariables[$key])" }
        $value = $Edition.webAccent.$key.ToLowerInvariant()
        $Text = [regex]::Replace($Text, $pattern, '${1}' + $value)
    }
    return $Text
}

function Get-EditionFiles {
    <# Files that make up the tree: tracked plus untracked-but-not-ignored when git is available. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)
    $Root = (Resolve-Path -LiteralPath $Root).Path
    $git = Get-Command git -ErrorAction SilentlyContinue
    $gitPath = if ($git) { $git.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\MinGit\cmd\git.exe' }
    if ((Test-Path -LiteralPath (Join-Path $Root '.git')) -and (Test-Path -LiteralPath $gitPath)) {
        $list = & $gitPath -C $Root -c core.quotepath=off ls-files --cached --others --exclude-standard
        if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
        return @($list | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $Root $_) -PathType Leaf) } |
            ForEach-Object { $_.Replace('/', '\') } | Sort-Object -Unique)
    }
    $skip = '\\(\.git|\.runtime|\.venv|__pycache__|bin|obj)(\\|$)'
    $prefix = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\').Length + 1
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($prefix) } |
        Where-Object { "\$_" -notmatch $skip } | Sort-Object)
}

function Test-EditionTextFile([string]$RelativePath) {
    $name = [System.IO.Path]::GetFileName($RelativePath).ToLowerInvariant()
    return ($script:TextExtensions -contains [System.IO.Path]::GetExtension($name)) -or ($script:TextNames -contains $name)
}

function Get-LineNumber([string]$Text, [int]$Index) { return ([regex]::Matches($Text.Substring(0, $Index), "`n")).Count + 1 }

function Get-ContentViews([string]$RelativePath, [byte[]]$Bytes) {
    <# Text files: one UTF-8 view. Binaries: Latin-1 plus UTF-16LE at both byte alignments. #>
    if (Test-EditionTextFile $RelativePath) { return ,@([System.Text.Encoding]::UTF8.GetString($Bytes)) }
    $views = @([System.Text.Encoding]::Latin1.GetString($Bytes), [System.Text.Encoding]::Unicode.GetString($Bytes))
    if ($Bytes.Length -gt 1) { $views += [System.Text.Encoding]::Unicode.GetString($Bytes, 1, $Bytes.Length - 1) }
    return ,$views
}

function Find-EditionLeaks {
    <# Case-insensitive search of relative paths and contents (text as UTF-8, binaries as Latin-1 and UTF-16). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string[]]$Token, [string[]]$Files)
    $Root = (Resolve-Path -LiteralPath $Root).Path
    if (-not $Files) { $Files = Get-EditionFiles -Root $Root }
    $hits = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $Files) {
        foreach ($t in $Token) {
            if ($relative.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hits.Add([pscustomobject]@{ File = $relative; Token = $t; Where = 'path' }) }
        }
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $Root $relative))
        $views = Get-ContentViews $relative $bytes
        foreach ($t in $Token) {
            foreach ($view in $views) {
                $index = $view.IndexOf($t, [StringComparison]::OrdinalIgnoreCase)
                if ($index -ge 0) {
                    $where = if ($views.Count -eq 1) { "line $(Get-LineNumber $view $index)" } else { "byte content" }
                    $hits.Add([pscustomobject]@{ File = $relative; Token = $t; Where = $where })
                    break
                }
            }
        }
    }
    return $hits.ToArray()
}

function Find-HygieneIssues {
    <# Absolute user-profile paths, credentials and private keys must never ship (binaries scanned as Latin-1 and UTF-16). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [string[]]$Files)
    $Root = (Resolve-Path -LiteralPath $Root).Path
    if (-not $Files) { $Files = Get-EditionFiles -Root $Root }
    # ASCII-only boundaries: in decoded binaries the neighbouring "character" is often a Unicode letter.
    $before = '(?<![A-Za-z0-9_])'; $after = '(?![A-Za-z0-9_])'
    $patterns = [ordered]@{
        'user-profile path' = "(?i)$before[A-Z]:\\Users\\(?!Public\\)[A-Za-z0-9._-]{2,}"
        'local user name' = "(?i)$before$([regex]::Escape($env:USERNAME))$after"
        'GitHub token' = "$before(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})"
        'private key' = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
        'AWS access key' = "${before}AKIA[0-9A-Z]{16}$after"
        'Azure storage key' = '(?i)(AccountKey|SharedAccessKey)=[A-Za-z0-9+/=]{20,}'
    }
    $issues = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $Files) {
        $isText = Test-EditionTextFile $relative
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $Root $relative))
        $views = Get-ContentViews $relative $bytes
        foreach ($name in $patterns.Keys) {
            $seen = $false
            foreach ($text in $views) {
                foreach ($m in [regex]::Matches($text, $patterns[$name])) {
                    $where = if ($isText) { "line $(Get-LineNumber $text $m.Index)" } else { 'byte content' }
                    $issues.Add([pscustomobject]@{ File = $relative; Issue = $name; Where = $where })
                    $seen = $true
                    if (-not $isText) { break }
                }
                if ($seen -and -not $isText) { break }
            }
        }
    }
    return $issues.ToArray()
}

function Write-TextFile([string]$Path, [string]$Text) {
    $null = [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path))
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Sync-EditionTree {
    <# Applies presentation fields. -Check reports drift instead of writing. Returns a list of differences. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [switch]$Check)
    $Root = (Resolve-Path -LiteralPath $Root).Path
    $edition = Read-Edition -Path (Join-Path $Root 'edition.json')
    $drift = [System.Collections.Generic.List[string]]::new()
    $targets = [ordered]@{
        'native\Brand.cs' = { param($current) Get-BrandSource -Edition $edition }
        'native\Assets\AppLogo.svg' = { param($current) Get-LogoSvg -Edition $edition }
        'native\App.xaml' = { param($current) Set-AccentInXaml -Text $current -Edition $edition }
        'style.css' = { param($current) Set-AccentInCss -Text $current -Edition $edition }
    }
    foreach ($relative in $targets.Keys) {
        $path = Join-Path $Root $relative
        $current = if (Test-Path -LiteralPath $path) { [System.IO.File]::ReadAllText($path) } else { '' }
        $expected = & $targets[$relative] $current
        if ($current.ReplaceLineEndings("`n") -ceq $expected.ReplaceLineEndings("`n")) { continue }
        if ($Check) { $drift.Add($relative) } else { Write-TextFile $path $expected }
    }
    if ($Check) {
        foreach ($asset in 'native\Assets\AppIcon.ico', 'native\Assets\AppLogo.png') {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $asset))) { $drift.Add("$asset (missing)") }
        }
    } else {
        & (Join-Path $Root 'tools\New-AppAssets.ps1') -Root $Root | Out-Null
    }
    $project = Join-Path $Root "native\$($edition.codeName).csproj"
    if (-not (Test-Path -LiteralPath $project)) { $drift.Add("native\$($edition.codeName).csproj (missing: project file must be named after codeName)") }
    return $drift.ToArray()
}

Export-ModuleMember -Function Get-Field, Read-Edition, Test-EditionManifest, ConvertTo-TreeManifest, Get-EditionIdentityMap, Convert-EditionText,
    Get-EditionRules, Get-BrandSource, Get-LogoSvg, Set-AccentInXaml, Set-AccentInCss, Get-EditionFiles, Test-EditionTextFile,
    Find-EditionLeaks, Find-HygieneIssues, Write-TextFile, Sync-EditionTree
