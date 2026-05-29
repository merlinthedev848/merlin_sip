param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectDir,

    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

function ConvertTo-WixId {
    param([string] $Value)
    $id = ($Value -replace '[^A-Za-z0-9_]', '_')
    if ($id -notmatch '^[A-Za-z_]') {
        $id = "I_$id"
    }

    if ($id.Length -gt 70) {
        $sha1 = [System.Security.Cryptography.SHA1]::Create()
        try {
            $hashBytes = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
        }
        finally {
            $sha1.Dispose()
        }

        $hash = [System.BitConverter]::ToString($hashBytes).Replace('-', '').Substring(0, 12)
        $id = "$($id.Substring(0, 55))_$hash"
    }

    return $id
}

function ConvertTo-StableGuid {
    param([string] $Value)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant()))
    }
    finally {
        $sha256.Dispose()
    }

    $guidBytes = New-Object byte[] 16
    [Array]::Copy($bytes, $guidBytes, 16)
    return ([Guid]::new($guidBytes)).ToString().ToUpperInvariant()
}

function Escape-Xml {
    param([string] $Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

function Get-RelativePath {
    param(
        [string] $BasePath,
        [string] $TargetPath
    )

    $base = (Resolve-Path -LiteralPath $BasePath).Path.TrimEnd('\') + '\'
    $target = (Resolve-Path -LiteralPath $TargetPath).Path
    if ($target.Equals($base.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        return '.'
    }

    if ($target.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        return $target.Substring($base.Length)
    }

    throw "Target path '$target' is not inside base path '$base'."
}

$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\')
$projectRoot = (Resolve-Path -LiteralPath $ProjectDir).Path.TrimEnd('\')
$files = Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
    Where-Object { $_.Name -ne 'MerlinSIP.exe' } |
    Sort-Object FullName

$directories = [ordered]@{
    '' = [pscustomobject]@{
        Id = 'INSTALLFOLDER'
        Name = ''
        Parent = $null
        Depth = 0
    }
}

foreach ($file in $files) {
    $relative = Get-RelativePath $publishRoot $file.DirectoryName
    if ($relative -eq '.') {
        continue
    }

    $parts = $relative -split '[\\/]'
    $path = ''
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $path = if ($path) { Join-Path $path $parts[$i] } else { $parts[$i] }
        if ($directories.Contains($path)) {
            continue
        }

        $parent = if ($i -eq 0) { '' } else { [string](Split-Path $path -Parent) }
        $directories[$path] = [pscustomobject]@{
            Id = "Dir_$(ConvertTo-WixId $path)"
            Name = $parts[$i]
            Parent = $parent
            Depth = $i + 1
        }
    }
}

$children = @{}
foreach ($entry in $directories.GetEnumerator()) {
    if ($null -eq $entry.Value.Parent) {
        continue
    }

    if (-not $children.ContainsKey($entry.Value.Parent)) {
        $children[$entry.Value.Parent] = New-Object System.Collections.Generic.List[string]
    }

    $children[$entry.Value.Parent].Add($entry.Key)
}

$filesByDir = @{}
foreach ($file in $files) {
    $relativeDir = Get-RelativePath $publishRoot $file.DirectoryName
    if ($relativeDir -eq '.') {
        $relativeDir = ''
    }

    if (-not $filesByDir.ContainsKey($relativeDir)) {
        $filesByDir[$relativeDir] = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    }

    $filesByDir[$relativeDir].Add($file)
}

$componentIds = New-Object System.Collections.Generic.List[string]
$builder = [System.Text.StringBuilder]::new()

function Add-DirectoryContent {
    param(
        [System.Text.StringBuilder] $Builder,
        [string] $RelativeDir,
        [int] $Indent
    )

    $pad = ' ' * $Indent

    if ($filesByDir.ContainsKey($RelativeDir)) {
        foreach ($file in $filesByDir[$RelativeDir]) {
            $relativeFile = Get-RelativePath $publishRoot $file.FullName
            $componentId = "Cmp_$(ConvertTo-WixId $relativeFile)"
            $fileId = "File_$(ConvertTo-WixId $relativeFile)"
            $guid = ConvertTo-StableGuid "MerlinSIP|$relativeFile"
            $componentIds.Add($componentId) | Out-Null

            [void] $Builder.AppendLine("$pad<Component Id=`"$componentId`" Guid=`"{$guid}`">")
            [void] $Builder.AppendLine("$pad  <File Id=`"$fileId`" Source=`"$(Escape-Xml $file.FullName)`" KeyPath=`"yes`" />")
            [void] $Builder.AppendLine("$pad</Component>")
        }
    }

    if ($children.ContainsKey($RelativeDir)) {
        foreach ($childKey in ($children[$RelativeDir] | Sort-Object)) {
            $child = $directories[$childKey]
            [void] $Builder.AppendLine("$pad<Directory Id=`"$($child.Id)`" Name=`"$(Escape-Xml $child.Name)`">")
            Add-DirectoryContent -Builder $Builder -RelativeDir $childKey -Indent ($Indent + 2)
            [void] $Builder.AppendLine("$pad</Directory>")
        }
    }
}

[void] $builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void] $builder.AppendLine('  <Package')
[void] $builder.AppendLine('    Name="Merlin SIP"')
[void] $builder.AppendLine('    Manufacturer="CK Media Services"')
[void] $builder.AppendLine('    Version="1.0.5"')
[void] $builder.AppendLine('    UpgradeCode="{8E5C2C6E-3A1E-4F83-9897-4E62EB06E0EC}"')
[void] $builder.AppendLine('    Scope="perMachine">')
[void] $builder.AppendLine('')
[void] $builder.AppendLine('    <MajorUpgrade DowngradeErrorMessage="A newer version of Merlin SIP is already installed." />')
[void] $builder.AppendLine('    <MediaTemplate EmbedCab="yes" />')
[void] $builder.AppendLine('')
[void] $builder.AppendLine("    <Icon Id=`"AppIcon.ico`" SourceFile=`"$(Escape-Xml (Join-Path $projectRoot 'MerlinSIP\Assets\CKMedia-Icon.ico'))`" />")
[void] $builder.AppendLine('    <Property Id="ARPPRODUCTICON" Value="AppIcon.ico" />')
[void] $builder.AppendLine('')
[void] $builder.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
[void] $builder.AppendLine('      <Directory Id="CompanyFolder" Name="CK Media Services">')
[void] $builder.AppendLine('        <Directory Id="INSTALLFOLDER" Name="Merlin SIP">')
[void] $builder.AppendLine('          <Component Id="MerlinSipExecutable" Guid="{55B7EF24-DAC2-4CD4-A58A-FF6B0D238F97}">')
[void] $builder.AppendLine("            <File Id=`"MerlinSipExe`" Source=`"$(Escape-Xml (Join-Path $publishRoot 'MerlinSIP.exe'))`" KeyPath=`"yes`">")
[void] $builder.AppendLine('              <Shortcut')
[void] $builder.AppendLine('                Id="StartMenuShortcut"')
[void] $builder.AppendLine('                Directory="ApplicationProgramsFolder"')
[void] $builder.AppendLine('                Name="Merlin SIP"')
[void] $builder.AppendLine('                WorkingDirectory="INSTALLFOLDER"')
[void] $builder.AppendLine('                Icon="AppIcon.ico"')
[void] $builder.AppendLine('                Advertise="yes" />')
[void] $builder.AppendLine('            </File>')
[void] $builder.AppendLine('          </Component>')
Add-DirectoryContent -Builder $builder -RelativeDir '' -Indent 10
[void] $builder.AppendLine('        </Directory>')
[void] $builder.AppendLine('      </Directory>')
[void] $builder.AppendLine('    </StandardDirectory>')
[void] $builder.AppendLine('')
[void] $builder.AppendLine('    <StandardDirectory Id="ProgramMenuFolder">')
[void] $builder.AppendLine('      <Directory Id="ApplicationProgramsFolder" Name="Merlin SIP">')
[void] $builder.AppendLine('        <Component Id="ApplicationProgramsFolderCleanup" Guid="{74F4EF38-5304-41F9-80AD-BE35983B67DC}">')
[void] $builder.AppendLine('          <RemoveFolder Id="RemoveApplicationProgramsFolder" On="uninstall" />')
[void] $builder.AppendLine('          <RegistryValue Root="HKCU" Key="Software\CK Media Services\Merlin SIP" Name="Installed" Type="integer" Value="1" KeyPath="yes" />')
[void] $builder.AppendLine('        </Component>')
[void] $builder.AppendLine('      </Directory>')
[void] $builder.AppendLine('    </StandardDirectory>')
[void] $builder.AppendLine('')
[void] $builder.AppendLine('    <Feature Id="MainFeature" Title="Merlin SIP" Level="1">')
[void] $builder.AppendLine('      <ComponentRef Id="MerlinSipExecutable" />')
foreach ($componentId in $componentIds) {
    [void] $builder.AppendLine("      <ComponentRef Id=`"$componentId`" />")
}
[void] $builder.AppendLine('      <ComponentRef Id="ApplicationProgramsFolderCleanup" />')
[void] $builder.AppendLine('    </Feature>')
[void] $builder.AppendLine('  </Package>')
[void] $builder.AppendLine('</Wix>')

Set-Content -LiteralPath $OutputPath -Value $builder.ToString() -Encoding UTF8
