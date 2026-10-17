# Original "device" logo style: a laptop outline with a four-point spark on a gradient tile.
# Units are a 256 x 256 canvas. Edition.psm1 (Get-LogoSvg) and New-AppAssets.ps1 (PNG/ICO) both draw from this
# file so the SVG, PNG and ICO stay identical. Fill/Stroke 'tile' = logo.background -> logo.backgroundEnd
# gradient; 'ink' = logo.foreground. MinSize omits a shape below that pixel size; MinStrokePixels keeps
# outlines legible at 16-24 px.
@{
    Device = @(
        @{ Kind = 'rect'; X = 9; Y = 9; W = 238; H = 238; R = 52; Fill = 'tile' }
        @{ Kind = 'rect'; X = 48; Y = 56; W = 160; H = 110; R = 14; Fill = 'ink'; FillOpacity = 0.14; Stroke = 'ink'; StrokeWidth = 13; MinStrokePixels = 1.4 }
        @{ Kind = 'rect'; X = 26; Y = 178; W = 204; H = 20; R = 10; Fill = 'ink' }
        @{ Kind = 'spark'; X = 128; Y = 111; R = 37; Waist = 0.18; Fill = 'ink' }
        @{ Kind = 'spark'; X = 179; Y = 82; R = 12; Waist = 0.18; Fill = 'ink'; MinSize = 32 }
    )
}
