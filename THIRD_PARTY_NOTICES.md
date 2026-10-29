# Third-party notices

Fermata Native FT Bridge builds on the work of the projects below.

## Pawlygon VRC Face Tracking

https://github.com/PawlygonStudio/VRC-Facetracking

This project follows many of Pawlygon's designs:

- The custom expression editor follows Pawlygon's Face Tracking Parameters component: an Override button on each forwarded parameter, a list of custom entries that replace detected ones by name, an input picker grouped by face region, a function menu, inserting at the cursor, and checking expressions as they are typed.
- Forwarding VRCFaceTracking's `v2/*` parameters to the animator with native face tracking follows the same approach, and the eye formulas match Pawlygon's.
- The eye fix follows how Pawlygon configures the avatar's Eye Look for native face tracking (Eye Gaze Mode and Eyelids set to Face Tracking).

```
MIT License

Copyright (c) 2024 Choki

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## VRCFaceTracking

https://github.com/benaclejames/VRCFaceTracking, licensed under the Apache License 2.0
(https://github.com/benaclejames/VRCFaceTracking/blob/master/LICENSE).

The `v2/*` formulas in `Editor/V2Formulas.cs` restate VRCFaceTracking's parameter definitions
(`UnifiedExpressionsParameters.cs` and `UnifiedSimpleExpressions.cs`) and its native eye encoding.
