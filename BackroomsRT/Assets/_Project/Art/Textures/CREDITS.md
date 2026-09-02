# Texture credits

All source textures are CC0 (public domain). No attribution is legally required,
but it is recorded here so the provenance stays traceable.

| Asset | Source | License | Used for |
|---|---|---|---|
| Carpet011 | https://ambientcg.com/view?id=Carpet011 | CC0 1.0 | Level 0 floor carpet |
| Carpet012 | https://ambientcg.com/view?id=Carpet012 | CC0 1.0 | alternate darker carpet (unused in M1) |
| OfficeCeiling001 | https://ambientcg.com/view?id=OfficeCeiling001 | CC0 1.0 | acoustic ceiling tiles |
| Wallpaper001B | https://ambientcg.com/view?id=Wallpaper001B | CC0 1.0 | mono-yellow wall base |
| Wallpaper001C | https://ambientcg.com/view?id=Wallpaper001C | CC0 1.0 | streaked/aged wall variant |
| Wallpaper002B | https://ambientcg.com/view?id=Wallpaper002B | CC0 1.0 | damp/mould wall variant |

Downloaded at 2K-JPG. Internal render resolution is 960x720 (VHS), so 2K is ample.

## Derived files (generated in this repo)

- `*_MaskMap.png` - HDRP mask map packed from the source maps
  (R = metallic, G = ambient occlusion, B = detail mask, A = smoothness = 1 - roughness).
- `Wallpaper*_Color_Seamed.png` / `Wallpaper*_NormalGL_Seamed.png` - wallpaper seams baked in
  at a 0.53 m pitch (the real width of a wallpaper roll), 1.5% darkening plus a small normal ridge.
- `DetailVariation.png` - large-scale fBm used as an HDRP detail map to break up texture
  repetition (R = albedo, G = normal Y, B = smoothness, A = normal X).
