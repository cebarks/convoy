// ponytail: minimal Unity type stubs for CI compilation — no copyrighted code
namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }

    public class Texture2D : Object
    {
        public Texture2D(int width, int height) { }
        public void SetPixel(int x, int y, Color color) { }
        public void Apply() { }
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color red => new Color(1, 0, 0, 1);
        public static Color green => new Color(0, 1, 0, 1);
        public static Color yellow => new Color(1, 1, 0, 1);
        public static Color white => new Color(1, 1, 1, 1);
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { this.x = x; this.y = y; this.width = width; this.height = height; }
    }

    public static class Time { public static float realtimeSinceStartup => 0f; }

    public static class Screen
    {
        public static int width => 1920;
        public static int height => 1080;
    }

    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }
    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }

    public class GUIStyleState
    {
        public Color textColor;
        public Texture2D? background;
    }
    public class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
        public int fontSize;
        public FontStyle fontStyle;
        public TextAnchor alignment;
        public GUIStyleState normal = new GUIStyleState();
    }

    public class GUISkin { public GUIStyle label = new GUIStyle(); }

    public static class GUI
    {
        public static GUISkin skin => new GUISkin();
        public static void Label(Rect position, string text, GUIStyle style) { }
        public static void Box(Rect position, string text, GUIStyle style) { }
        public static bool Toggle(Rect position, bool value, string text) => value;
        public static bool Button(Rect position, string text) => false;
        public static bool Button(Rect position, string text, GUIStyle style) => false;
        public static void DrawTexture(Rect position, Texture2D image) { }
        public static Vector2 BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect) => scrollPosition;
        public static void EndScrollView() { }
    }
}
