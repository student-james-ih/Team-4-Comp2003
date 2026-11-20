Basic terrain generation for the flight sim,

Contains customisable settings for: Resolution, Width, Height Cap, Seed, Noise Scale, Octaves, Persistence, Lacunarity Offset and Height Curve.

Also contains settings for: Mountain Amount, Mountain Scale, Mountain Sharpness, Mountain Sharpness, Mountain Octaves, Mountain Height, Ridge Sharpness, Mountain Persistence and Smoothing Passes.

Resolution must abide to the formula: 2^n + 1 (e.g. 129, 257, 513, 1025) where n is an int (e.g 7, 8, 9, 10). Otherwise unity rounds it and part of the terrain remains flat.

I would keep height above 100, to make sure mountains generate as intended, but can be changed if a flatter terrain is desired.
