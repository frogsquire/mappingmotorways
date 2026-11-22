using System.Text;
using Motorways;
using Motorways.Audio;
using Motorways.Views;
using Server;
using UnityEngine;
using UnityEngine.UI;

namespace RouteViz
{
    /// <summary>A round in-game button next to the top-left back arrow that shows/hides all routes (like Alt+C).</summary>
    public partial class RouteVizPlugin
    {
        private bool _buttonProbed;
        private float _nextButtonProbe;

        /// <summary>Temporary: logs the back button's hierarchy so the clone can reuse the right parts.</summary>
        private void ProbeBackButton()
        {
            if (_buttonProbed || Time.unscaledTime < _nextButtonProbe)
                return;
            _nextButtonProbe = Time.unscaledTime + 2f;

            GameUIScreen screen = null;
            City city = Get.City;
            if (city != null && city.Scope != null)
                screen = city.Scope.Get<GameUIScreen>();
            if (screen == null)
                screen = Object.FindObjectOfType<GameUIScreen>();
            if (screen == null || screen.backButton == null)
            {
                Logger.LogInfo("[probe] GameUIScreen/backButton not found yet.");
                return;
            }
            _buttonProbed = true;

            var sb = new StringBuilder();
            Transform t = screen.backButton.transform;
            sb.Append("[probe] back button parent chain: ");
            for (Transform p = t.parent; p != null; p = p.parent)
                sb.Append(p.name).Append(" < ");
            sb.AppendLine();
            if (t.parent != null)
                foreach (Transform sibling in t.parent)
                    sb.AppendLine($"  sibling {sibling.name} active={sibling.gameObject.activeSelf} {Describe(sibling)}");
            Dump(t, 0, sb);
            Logger.LogInfo(sb.ToString());
        }

        private static void Dump(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(new string(' ', depth * 2)).Append("- ").Append(t.name)
              .Append(" active=").Append(t.gameObject.activeSelf).Append(' ').Append(Describe(t)).AppendLine();
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform)
                    continue;
                sb.Append(new string(' ', depth * 2 + 4)).Append(c.GetType().FullName);
                if (c is Image img)
                    sb.Append($" sprite={(img.sprite != null ? img.sprite.name : "null")} color={img.color} type={img.type}");
                sb.AppendLine();
            }
            if (depth < 6)
                foreach (Transform child in t)
                    Dump(child, depth + 1, sb);
        }

        private static string Describe(Transform t)
        {
            return t is RectTransform r
                ? $"pos={r.anchoredPosition} size={r.sizeDelta} anchors={r.anchorMin}-{r.anchorMax} pivot={r.pivot} scale={r.localScale}"
                : "";
        }
    }
}
