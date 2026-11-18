Basic terrain generation for the flight sim,

Contains customisable settings for: Resolution, Width, Height Cap, Seed, Noise Scale, Octaves, Persistence, Lacunarity Offset and Height Curve.

Resolution must abide to the formula: 2^n + 1 (e.g. 129, 257, 513, 1025) where n is an int (e.g 7, 8, 9, 10). Otherwise unity rounds it and part of the terrain remains flat.
