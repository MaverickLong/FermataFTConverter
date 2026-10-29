using System;
using System.Collections.Generic;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>
    /// VRCFaceTracking's "v2/*" avatar parameters, restated as native face tracking expressions over
    /// Unified Expressions tracking keys.
    /// </summary>
    /// <remarks>
    /// Transcribed from VRCFaceTracking (https://github.com/benaclejames/VRCFaceTracking, Apache-2.0):
    /// UnifiedExpressionsParameters.cs and UnifiedSimpleExpressions.cs.
    /// VRCFT's native encoder splits each gaze axis into a complementary key pair
    /// (EyeLookInLeft = max(left gaze x, 0), EyeLookOutLeft = max(-left gaze x, 0), and so on)
    /// and sends EyeClosed = 1 - openness, which is what the eye formulas below undo.
    /// VRCFT's pupil diameter parameters have no native key equivalent and are not listed.
    /// </remarks>
    internal static class V2Formulas
    {
        public static bool TryGet(string v2Name, out string expression)
        {
            return Table.TryGetValue(v2Name, out expression);
        }

        const string SmileRight = "0.8*MouthCornerPullRight + 0.2*MouthCornerSlantRight";
        const string SmileLeft = "0.8*MouthCornerPullLeft + 0.2*MouthCornerSlantLeft";
        const string SadRight = "max(MouthFrownRight, MouthStretchRight)";
        const string SadLeft = "max(MouthFrownLeft, MouthStretchLeft)";

        static readonly Dictionary<string, string> Table = Build();

        static Dictionary<string, string> Build()
        {
            var t = new Dictionary<string, string>(StringComparer.Ordinal);

            // Every Unified Expressions shape is also published unchanged as v2/<Shape>.
            foreach (var key in NativeExpression.TrackingKeys) t[key] = key;

            // Eye gaze
            t["EyeX"] = "(EyeLookInLeft - EyeLookOutLeft + EyeLookOutRight - EyeLookInRight) * 0.5";
            t["EyeY"] = "(EyeLookUpLeft - EyeLookDownLeft + EyeLookUpRight - EyeLookDownRight) * 0.5";
            t["EyeLeftX"] = "EyeLookInLeft - EyeLookOutLeft";
            t["EyeLeftY"] = "EyeLookUpLeft - EyeLookDownLeft";
            t["EyeRightX"] = "EyeLookOutRight - EyeLookInRight";
            t["EyeRightY"] = "EyeLookUpRight - EyeLookDownRight";

            // Eye openness, lids, widen, squint
            t["EyeOpenLeft"] = "1 - EyeClosedLeft";
            t["EyeOpenRight"] = "1 - EyeClosedRight";
            t["EyeOpen"] = "1 - (EyeClosedLeft + EyeClosedRight) * 0.5";
            t["EyeClosed"] = "(EyeClosedLeft + EyeClosedRight) * 0.5";
            t["EyeWide"] = "max(EyeWideLeft, EyeWideRight)";
            t["EyeLidLeft"] = "0.75 - 0.75*EyeClosedLeft + 0.25*EyeWideLeft";
            t["EyeLidRight"] = "0.75 - 0.75*EyeClosedRight + 0.25*EyeWideRight";
            t["EyeLid"] = "0.75 - 0.375*(EyeClosedLeft + EyeClosedRight) + 0.125*(EyeWideLeft + EyeWideRight)";
            t["EyeSquint"] = "max(EyeSquintLeft, EyeSquintRight)";
            t["EyesSquint"] = "max(EyeSquintLeft, EyeSquintRight)";

            // Brows. VRCFT's min(1, ...) around the inner/outer average never binds, so it is omitted.
            t["BrowUpRight"] = "0.6*BrowOuterUpRight + 0.4*BrowInnerUpRight";
            t["BrowUpLeft"] = "0.6*BrowOuterUpLeft + 0.4*BrowInnerUpLeft";
            t["BrowDownRight"] = "0.75*BrowLowererRight + 0.25*BrowPinchRight";
            t["BrowDownLeft"] = "0.75*BrowLowererLeft + 0.25*BrowPinchLeft";
            t["BrowUp"] = "0.3*(BrowOuterUpRight + BrowOuterUpLeft) + 0.2*(BrowInnerUpRight + BrowInnerUpLeft)";
            t["BrowDown"] = "0.375*(BrowLowererRight + BrowLowererLeft) + 0.125*(BrowPinchRight + BrowPinchLeft)";
            t["BrowInnerUp"] = "(BrowInnerUpLeft + BrowInnerUpRight) * 0.5";
            t["BrowOuterUp"] = "(BrowOuterUpLeft + BrowOuterUpRight) * 0.5";
            t["BrowExpressionRight"] = "(BrowInnerUpRight + BrowOuterUpRight) * 0.5 - 0.75*BrowLowererRight - 0.25*BrowPinchRight";
            t["BrowExpressionLeft"] = "(BrowInnerUpLeft + BrowOuterUpLeft) * 0.5 - 0.75*BrowLowererLeft - 0.25*BrowPinchLeft";
            t["BrowExpression"] = "((BrowInnerUpRight + BrowOuterUpRight + BrowInnerUpLeft + BrowOuterUpLeft) * 0.5"
                                  + " - 0.75*(BrowLowererRight + BrowLowererLeft) - 0.25*(BrowPinchRight + BrowPinchLeft)) * 0.5";

            // Jaw
            t["JawX"] = "JawRight - JawLeft";
            t["JawZ"] = "JawForward - JawBackward";

            // Cheeks
            t["CheekSquint"] = "(CheekSquintLeft + CheekSquintRight) * 0.5";
            t["CheekPuffSuckLeft"] = "CheekPuffLeft - CheekSuckLeft";
            t["CheekPuffSuckRight"] = "CheekPuffRight - CheekSuckRight";
            t["CheekPuffSuck"] = "(CheekPuffRight + CheekPuffLeft - CheekSuckRight - CheekSuckLeft) * 0.5";
            t["CheekSuck"] = "(CheekSuckLeft + CheekSuckRight) * 0.5";

            // Mouth direction
            t["MouthUpperX"] = "MouthUpperRight - MouthUpperLeft";
            t["MouthLowerX"] = "MouthLowerRight - MouthLowerLeft";
            t["MouthX"] = "(MouthUpperRight + MouthLowerRight - MouthUpperLeft - MouthLowerLeft) * 0.5";

            // Lips
            t["LipSuckUpper"] = "(LipSuckUpperRight + LipSuckUpperLeft) * 0.5";
            t["LipSuckLower"] = "(LipSuckLowerRight + LipSuckLowerLeft) * 0.5";
            t["LipSuck"] = "(LipSuckUpperRight + LipSuckUpperLeft + LipSuckLowerRight + LipSuckLowerLeft) * 0.25";
            t["LipFunnelUpper"] = "(LipFunnelUpperRight + LipFunnelUpperLeft) * 0.5";
            t["LipFunnelLower"] = "(LipFunnelLowerRight + LipFunnelLowerLeft) * 0.5";
            t["LipFunnel"] = "(LipFunnelUpperRight + LipFunnelUpperLeft + LipFunnelLowerRight + LipFunnelLowerLeft) * 0.25";
            t["LipPuckerUpper"] = "(LipPuckerUpperRight + LipPuckerUpperLeft) * 0.5";
            t["LipPuckerLower"] = "(LipPuckerLowerRight + LipPuckerLowerLeft) * 0.5";
            t["LipPuckerRight"] = "(LipPuckerUpperRight + LipPuckerLowerRight) * 0.5";
            t["LipPuckerLeft"] = "(LipPuckerUpperLeft + LipPuckerLowerLeft) * 0.5";
            t["LipPucker"] = "(LipPuckerUpperRight + LipPuckerUpperLeft + LipPuckerLowerRight + LipPuckerLowerLeft) * 0.25";
            t["LipSuckFunnelUpper"] = "(LipSuckUpperRight + LipSuckUpperLeft - LipFunnelUpperRight - LipFunnelUpperLeft) * 0.5";
            t["LipSuckFunnelLower"] = "(LipSuckLowerRight + LipSuckLowerLeft - LipFunnelLowerRight - LipFunnelLowerLeft) * 0.5";
            t["LipSuckFunnelLowerLeft"] = "LipSuckLowerLeft - LipFunnelLowerLeft";
            t["LipSuckFunnelLowerRight"] = "LipSuckLowerRight - LipFunnelLowerRight";
            t["LipSuckFunnelUpperLeft"] = "LipSuckUpperLeft - LipFunnelUpperLeft";
            t["LipSuckFunnelUpperRight"] = "LipSuckUpperRight - LipFunnelUpperRight";

            // Mouth
            t["MouthUpperUp"] = "(MouthUpperUpRight + MouthUpperUpLeft) * 0.5";
            t["MouthLowerDown"] = "(MouthLowerDownRight + MouthLowerDownLeft) * 0.5";
            t["MouthOpen"] = "(MouthUpperUpRight + MouthUpperUpLeft + MouthLowerDownRight + MouthLowerDownLeft) * 0.25";
            t["MouthStretch"] = "(MouthStretchRight + MouthStretchLeft) * 0.5";
            t["MouthTightener"] = "(MouthTightenerRight + MouthTightenerLeft) * 0.5";
            t["MouthPress"] = "(MouthPressRight + MouthPressLeft) * 0.5";
            t["MouthDimple"] = "(MouthDimpleRight + MouthDimpleLeft) * 0.5";
            t["NoseSneer"] = "(NoseSneerRight + NoseSneerLeft) * 0.5";
            t["MouthTightenerStretch"] = "(MouthTightenerRight + MouthTightenerLeft - MouthStretchRight - MouthStretchLeft) * 0.5";
            t["MouthTightenerStretchLeft"] = "MouthTightenerLeft - MouthStretchLeft";
            t["MouthTightenerStretchRight"] = "MouthTightenerRight - MouthStretchRight";

            // Lip corners: smile = 0.8 pull + 0.2 slant, sad = max(frown, stretch)
            t["MouthSmileRight"] = SmileRight;
            t["MouthSmileLeft"] = SmileLeft;
            t["MouthSadRight"] = SadRight;
            t["MouthSadLeft"] = SadLeft;
            t["MouthCornerYLeft"] = "MouthCornerSlantLeft - MouthFrownLeft";
            t["MouthCornerYRight"] = "MouthCornerSlantRight - MouthFrownRight";
            t["MouthCornerY"] = "(MouthCornerSlantLeft - MouthFrownLeft + MouthCornerSlantRight - MouthFrownRight) * 0.5";
            t["SmileFrownRight"] = SmileRight + " - MouthFrownRight";
            t["SmileFrownLeft"] = SmileLeft + " - MouthFrownLeft";
            t["SmileFrown"] = "(" + SmileRight + " + " + SmileLeft + " - MouthFrownRight - MouthFrownLeft) * 0.5";
            t["SmileSadRight"] = SmileRight + " - " + SadRight;
            t["SmileSadLeft"] = SmileLeft + " - " + SadLeft;
            t["SmileSad"] = "(" + SmileLeft + " + " + SmileRight + " - " + SadLeft + " - " + SadRight + ") * 0.5";

            // Tongue
            t["TongueX"] = "TongueRight - TongueLeft";
            t["TongueY"] = "TongueUp - TongueDown";
            t["TongueArchY"] = "TongueCurlUp - TongueBendDown";
            t["TongueShape"] = "TongueFlat - TongueSquish";

            return t;
        }
    }
}
