# Icon sources

`../j0kers.ico` is built from these three SVGs. Three drawings rather than
one because detail that reads at 256 px turns to mush by 16 px:

| sizes | source | why |
|---|---|---|
| 16, 24 | `j0kers-tiny.svg` | subject drawn nearly frame-filling, four flat colours, no gradients |
| 32, 48 | `j0kers-small.svg` | simplified shapes, still inside the tile |
| 64, 128, 256 | `j0kers-large.svg` | full detail: gradients, horn rim light, teeth |

The horns are solid wedges in all three — a thin crescent survives at 256
and disappears by 32, so the silhouette stays the same and only the shading
changes with size.

## Rebuilding

Rasterise each SVG to `ico-<size>.png` at the sizes above (any renderer),
then:

```powershell
./build-ico.ps1 -Root <folder with the PNGs> -Out ../j0kers.ico
```

That writes a PNG-compressed ICO, which is what Windows has used since
Vista and what the previous icon used too.
