using System.Collections.Generic;
using UnityEngine;

namespace RouteViz
{
    /// <summary>
    /// One stretch of route drawn in the game's style: a band as wide as a car in the route colour (its edges read as
    /// borders), a paler middle, and chevrons along the middle pointing the way the car travels. Built as a mesh with
    /// vertex colours because this build strips LineRenderer texture tiling, so arrows can't be a scrolling texture.
    /// </summary>
    internal sealed class RouteMesh
    {
        private const float InnerFraction = 0.56f;   // pale middle width / full width
        private const float PaleAmount = 0.6f;       // how far the middle is lightened toward white
        private const float MaxMiter = 2f;

        public readonly GameObject Object;
        public readonly MeshRenderer Renderer;
        private readonly Mesh _mesh;

        private static readonly List<Vector3> Verts = new List<Vector3>(1024);
        private static readonly List<Color32> Colors = new List<Color32>(1024);
        private static readonly List<int> Tris = new List<int>(2048);
        private static readonly List<float> Along = new List<float>(256);

        public RouteMesh(Material material)
        {
            Object = new GameObject("RouteViz_Route");
            var filter = Object.AddComponent<MeshFilter>();
            Renderer = Object.AddComponent<MeshRenderer>();
            Renderer.sharedMaterial = material;
            Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Renderer.receiveShadows = false;
            _mesh = new Mesh { name = "RouteViz_RouteMesh" };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;
        }

        /// <summary>
        /// Rebuilds the mesh along the points (local space). us[i] is each point's arrow coordinate (arrows sit where
        /// u - cycle is a whole number), so arrows are placed by the caller's lane-pinned spacing.
        /// </summary>
        public void Build(List<Vector3> points, List<float> us, Color color, float width, float cycle)
        {
            Verts.Clear();
            Colors.Clear();
            Tris.Clear();

            var outer = (Color32)new Color(color.r, color.g, color.b, 1f);
            Color paleColor = Color.Lerp(color, Color.white, PaleAmount);
            var pale = (Color32)new Color(paleColor.r, paleColor.g, paleColor.b, 1f);

            // Triangles draw in order (the shader doesn't write depth): band, then middle, then arrows on top.
            Ribbon(points, width * 0.5f, outer);
            Ribbon(points, width * 0.5f * InnerFraction, pale);
            Arrows(points, us, width, cycle, outer);

            _mesh.Clear();
            _mesh.SetVertices(Verts);
            _mesh.SetColors(Colors);
            _mesh.SetTriangles(Tris, 0);
            _mesh.RecalculateBounds();
        }

        public void Destroy()
        {
            UnityEngine.Object.Destroy(_mesh);
            UnityEngine.Object.Destroy(Object);
        }

        private static void Ribbon(List<Vector3> pts, float halfWidth, Color32 color)
        {
            int n = pts.Count;
            int start = Verts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 dirIn = i > 0 ? Dir(pts[i - 1], pts[i]) : Dir(pts[i], pts[Mathf.Min(i + 1, n - 1)]);
                Vector2 dirOut = i < n - 1 ? Dir(pts[i], pts[i + 1]) : dirIn;
                Vector2 tangent = (dirIn + dirOut);
                tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : dirOut;
                var normal = new Vector2(-tangent.y, tangent.x);
                // Miter: keep the band's width constant through bends.
                var segNormal = new Vector2(-dirOut.y, dirOut.x);
                float dot = Vector2.Dot(normal, segNormal);
                float miter = dot > 1f / MaxMiter ? 1f / dot : MaxMiter;
                Vector2 offset = normal * (halfWidth * miter);
                Vector3 p = pts[i];
                Verts.Add(new Vector3(p.x + offset.x, p.y + offset.y, p.z));
                Verts.Add(new Vector3(p.x - offset.x, p.y - offset.y, p.z));
                Colors.Add(color);
                Colors.Add(color);
            }
            for (int i = 0; i < n - 1; i++)
            {
                int a = start + i * 2;
                Tris.Add(a); Tris.Add(a + 2); Tris.Add(a + 1);
                Tris.Add(a + 1); Tris.Add(a + 2); Tris.Add(a + 3);
            }
        }

        /// <summary>Chevrons (">") wherever u - cycle is a whole number, each drawn by exactly one segment.</summary>
        private static void Arrows(List<Vector3> pts, List<float> us, float width, float cycle, Color32 color)
        {
            Along.Clear();
            float total = 0f;
            Along.Add(0f);
            for (int i = 1; i < pts.Count; i++)
            {
                total += Vector2.Distance(pts[i - 1], pts[i]);
                Along.Add(total);
            }
            if (total <= 0f)
                return;

            float halfLength = width * 0.24f;  // tip to back, halved
            float halfSpan = width * 0.26f;    // arms reach this far either side of the middle (pale band: 0.28)
            float stroke = width * 0.17f;      // arm thickness: bold, so arrows still read zoomed out

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float u0 = us[i], u1 = us[i + 1];
                if (u1 <= u0)
                    continue;
                // Half-open [u0, u1): an arrow on a shared point belongs to the segment that starts there.
                for (float k = Mathf.Ceil(u0 - cycle); k + cycle < u1; k++)
                {
                    float u = k + cycle;
                    if (u < u0)
                        continue;
                    float s = Mathf.Lerp(Along[i], Along[i + 1], (u - u0) / (u1 - u0));
                    Vector3 p = Sample(pts, s);
                    // Heading from points either side, so arrows turn smoothly through bends.
                    Vector2 t = Dir(Sample(pts, s - halfLength), Sample(pts, s + halfLength));
                    var nrm = new Vector2(-t.y, t.x);

                    Vector2 c = p;
                    Vector2 tip = c + t * halfLength;
                    Vector2 left = c - t * halfLength + nrm * halfSpan;
                    Vector2 right = c - t * halfLength - nrm * halfSpan;
                    Stroke(left, tip, stroke, p.z, color);
                    Stroke(right, tip, stroke, p.z, color);
                }
            }
        }

        /// <summary>Point at distance s along the polyline (uses Along), extrapolated past the ends.</summary>
        private static Vector3 Sample(List<Vector3> pts, float s)
        {
            int n = pts.Count;
            if (s <= 0f)
                return pts[0] + (Vector3)(Dir(pts[0], pts[1]) * s);
            if (s >= Along[n - 1])
                return pts[n - 1] + (Vector3)(Dir(pts[n - 2], pts[n - 1]) * (s - Along[n - 1]));
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (Along[mid] <= s) lo = mid; else hi = mid;
            }
            float segLen = Along[hi] - Along[lo];
            return Vector3.Lerp(pts[lo], pts[hi], segLen > 1e-6f ? (s - Along[lo]) / segLen : 0f);
        }

        private static void Stroke(Vector2 a, Vector2 b, float thickness, float z, Color32 color)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-10f)
                return;
            d.Normalize();
            Vector2 o = new Vector2(-d.y, d.x) * (thickness * 0.5f);
            // Extend past the tip a little so the two arms meet in a point rather than a notch.
            Vector2 b2 = b + d * (thickness * 0.5f);
            int i = Verts.Count;
            Verts.Add(new Vector3(a.x + o.x, a.y + o.y, z));
            Verts.Add(new Vector3(a.x - o.x, a.y - o.y, z));
            Verts.Add(new Vector3(b2.x + o.x, b2.y + o.y, z));
            Verts.Add(new Vector3(b2.x - o.x, b2.y - o.y, z));
            for (int k = 0; k < 4; k++)
                Colors.Add(color);
            Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 1);
            Tris.Add(i + 1); Tris.Add(i + 2); Tris.Add(i + 3);
        }

        private static Vector2 Dir(Vector3 a, Vector3 b)
        {
            var d = new Vector2(b.x - a.x, b.y - a.y);
            float m = d.magnitude;
            return m > 1e-6f ? d / m : Vector2.right;
        }
    }
}
