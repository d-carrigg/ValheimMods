using System.Collections.Generic;
using UnityEngine;

namespace SignSorter
{
    /// <summary>Which chest a sign belongs to. Pure math so it can be tested outside the game.</summary>
    internal static class SignGeometry
    {
        /// <summary>Arrow must point within this many degrees of the chest.</summary>
        public const float MaxArrowAngle = 60f;
        public const float MinVerticalReach = 0.5f;

        /// <summary>
        /// Picks the chest an arrow sign points at: within <paramref name="range"/>, within 60° of
        /// the arrow, preferring close chests that are straight along the arrow. Returns -1 if none.
        /// </summary>
        /// <param name="readerRight">The reader's right-hand direction when facing the sign.</param>
        public static int ChooseArrowTarget(Vector3 signPos, Arrow arrow, Vector3 readerRight, IList<Vector3> chestPoints, float range)
        {
            if (arrow == Arrow.None || chestPoints == null) return -1;

            Vector3 right = new Vector3(readerRight.x, 0f, readerRight.z);
            if (right.sqrMagnitude < 1e-6f && (arrow == Arrow.Left || arrow == Arrow.Right)) return -1;
            right = right.normalized;

            Vector3 dir;
            switch (arrow)
            {
                case Arrow.Left: dir = -right; break;
                case Arrow.Right: dir = right; break;
                case Arrow.Up: dir = Vector3.up; break;
                default: dir = Vector3.down; break;
            }
            bool horizontal = arrow == Arrow.Left || arrow == Arrow.Right;
            float minCos = Mathf.Cos(MaxArrowAngle * Mathf.Deg2Rad);

            int best = -1;
            float bestCost = float.MaxValue;
            for (int i = 0; i < chestPoints.Count; i++)
            {
                Vector3 v = chestPoints[i] - signPos;
                float dist = v.magnitude;
                if (dist > range || dist < 0.05f) continue;

                float cos;
                if (horizontal)
                {
                    // Left/right ignores height (chest below a sign on a shelf is still "left").
                    Vector3 flat = new Vector3(v.x, 0f, v.z);
                    if (flat.magnitude < 0.2f) continue;
                    cos = Vector3.Dot(flat.normalized, dir);
                }
                else
                {
                    // Up/down must actually reach the next chest along the arrow, not pick the
                    // chest the sign is mounted on (whose middle is only ~0.3 m away).
                    if (Vector3.Dot(v, dir) < MinVerticalReach) continue;
                    cos = Vector3.Dot(v / dist, dir);
                }
                if (cos < minCos) continue;

                float cost = dist / cos;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = i;
                }
            }
            return best;
        }

        internal struct SignInfo
        {
            public Vector3 Position;
            public Arrow Arrow;
            public Vector3 ReaderRight;
            public string Body;
        }

        /// <summary>
        /// Gives each chest the text of the signs that label it. An arrow sign labels only the
        /// chest it points at. A plain sign labels only the one chest closest to it (so an
        /// unsigned chest next door doesn't inherit it), unless an arrow already claimed that chest.
        /// Pass every chest near the signs, including ones you won't sort, so labels don't
        /// shift when a chest is out of range or off-limits.
        /// </summary>
        public static List<List<string>> AssignLabels(IList<SignInfo> signs, IList<Vector3> chestPoints, float signRange, float arrowRange)
        {
            var labels = new List<List<string>>(chestPoints.Count);
            for (int i = 0; i < chestPoints.Count; i++) labels.Add(new List<string>());
            var arrowed = new bool[chestPoints.Count];

            foreach (SignInfo sign in signs)
            {
                if (sign.Arrow == Arrow.None) continue;
                int target = ChooseArrowTarget(sign.Position, sign.Arrow, sign.ReaderRight, chestPoints, arrowRange);
                if (target < 0) continue;
                labels[target].Add(sign.Body ?? "");
                arrowed[target] = true;
            }

            foreach (SignInfo sign in signs)
            {
                if (sign.Arrow != Arrow.None) continue;
                int nearest = Nearest(sign.Position, chestPoints, signRange);
                if (nearest >= 0 && !arrowed[nearest]) labels[nearest].Add(sign.Body ?? "");
            }
            return labels;
        }

        /// <summary>Index of the closest point within range of <paramref name="from"/>, or -1.</summary>
        public static int Nearest(Vector3 from, IList<Vector3> points, float range)
        {
            int best = -1;
            float bestSq = range * range;
            for (int i = 0; i < points.Count; i++)
            {
                float d = (points[i] - from).sqrMagnitude;
                if (d <= bestSq)
                {
                    bestSq = d;
                    best = i;
                }
            }
            return best;
        }
    }
}
