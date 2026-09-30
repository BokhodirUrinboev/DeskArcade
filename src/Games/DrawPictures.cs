using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskArcade.Games;

/// <summary>
/// The pictures the computer draws in Draw &amp; Guess, for the words it knows how to draw: a few strokes each, made of
/// lines, circles, arcs and polygons on the same 0–255 board a person draws on, drawn one stroke at a time. The words
/// are keyed by their English form in draw/words.json. Colours are indexes into the game's palette
/// (<see cref="DrawGame.Palette"/>): 0 ink, 1 red, 2 blue, 3 green, 4 orange, 5 brown.
/// </summary>
public static class DrawPictures
{
    const int Ink = 0, Red = 1, Blue = 2, Green = 3, Orange = 4, Brown = 5;

    static IEnumerable<(double, double)> Circle(double cx, double cy, double r, int n = 28) =>
        Enumerable.Range(0, n + 1).Select(i => (cx + r * Math.Cos(2 * Math.PI * i / n), cy + r * Math.Sin(2 * Math.PI * i / n)));

    static IEnumerable<(double, double)> Arc(double cx, double cy, double r, double fromDeg, double toDeg, int n = 16) =>
        Enumerable.Range(0, n + 1).Select(i =>
        {
            double a = (fromDeg + (toDeg - fromDeg) * i / n) * Math.PI / 180;
            return (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        });

    static IEnumerable<(double, double)> Poly(params double[] xy) => Enumerable.Range(0, xy.Length / 2).Select(i => (xy[i * 2], xy[i * 2 + 1]));

    static IEnumerable<(double, double)> Rect(double x, double y, double w, double h) => Poly(x, y, x + w, y, x + w, y + h, x, y + h, x, y);

    static DrawStroke S(int color, IEnumerable<(double X, double Y)> points, int size = 1)
    {
        var list = points.ToList();
        var bytes = new byte[list.Count * 2];
        for (int i = 0; i < list.Count; i++)
        {
            bytes[i * 2] = (byte)Math.Clamp(Math.Round(list[i].X), 0, 255);
            bytes[i * 2 + 1] = (byte)Math.Clamp(Math.Round(list[i].Y), 0, 255);
        }
        return new DrawStroke(color, size, bytes);
    }

    static DrawStroke[] P(params DrawStroke[] strokes) => strokes;

    /// <summary>The pictures by English word.</summary>
    public static readonly IReadOnlyDictionary<string, DrawStroke[]> All = new Dictionary<string, DrawStroke[]>(StringComparer.Ordinal)
    {
        ["sun"] = P(S(Orange, Circle(128, 128, 42), 2),
            S(Orange, Poly(128, 40, 128, 18)), S(Orange, Poly(128, 216, 128, 238)), S(Orange, Poly(40, 128, 18, 128)), S(Orange, Poly(216, 128, 238, 128)),
            S(Orange, Poly(66, 66, 50, 50)), S(Orange, Poly(190, 66, 206, 50)), S(Orange, Poly(66, 190, 50, 206)), S(Orange, Poly(190, 190, 206, 206))),
        ["moon"] = P(S(Orange, Arc(128, 128, 80, 60, 300, 32).Concat(Arc(160, 118, 64, 250, 110, 28)), 2)),
        ["star"] = P(S(Orange, Poly(128, 30, 152, 100, 226, 102, 166, 146, 188, 220, 128, 176, 68, 220, 90, 146, 30, 102, 104, 100, 128, 30), 2)),
        ["cloud"] = P(S(Blue, Arc(88, 150, 34, 90, 270).Concat(Arc(112, 108, 38, 180, 320)).Concat(Arc(166, 104, 44, 200, 360)).Concat(Arc(190, 148, 34, 270, 450)).Concat(Poly(190, 182, 88, 184)), 2)),
        ["rain"] = P(S(Blue, Arc(96, 100, 30, 90, 270).Concat(Arc(128, 72, 36, 180, 360)).Concat(Arc(164, 100, 30, 270, 450)).Concat(Poly(164, 130, 96, 130)), 2),
            S(Blue, Poly(90, 160, 80, 190)), S(Blue, Poly(128, 160, 118, 196)), S(Blue, Poly(166, 160, 156, 190)), S(Blue, Poly(110, 200, 100, 230)), S(Blue, Poly(150, 205, 140, 235))),
        ["snowman"] = P(S(Ink, Circle(128, 176, 52)), S(Ink, Circle(128, 90, 36)), S(Ink, Circle(116, 82, 3, 8)), S(Ink, Circle(140, 82, 3, 8)),
            S(Orange, Poly(128, 92, 156, 98, 128, 102), 2), S(Ink, Poly(88, 150, 44, 120)), S(Ink, Poly(168, 150, 212, 120))),
        ["tree"] = P(S(Brown, Rect(112, 160, 32, 70), 2), S(Green, Circle(128, 110, 64), 2)),
        ["flower"] = P(S(Green, Poly(128, 140, 128, 236), 2), S(Green, Arc(150, 196, 22, 180, 360)),
            S(Red, Circle(128, 70, 22)), S(Red, Circle(128, 138, 22)), S(Red, Circle(94, 104, 22)), S(Red, Circle(162, 104, 22)), S(Orange, Circle(128, 104, 14), 2)),
        ["house"] = P(S(Ink, Rect(56, 118, 144, 110), 2), S(Red, Poly(40, 124, 128, 40, 216, 124), 2), S(Brown, Rect(110, 170, 36, 58)), S(Blue, Rect(70, 138, 30, 26)), S(Blue, Rect(156, 138, 30, 26))),
        ["heart"] = P(S(Red, Arc(92, 96, 38, 150, 360).Concat(Arc(164, 96, 38, 180, 390)).Concat(Poly(128, 214)).Concat(Poly(58, 118)), 3)),
        ["circle"] = P(S(Ink, Circle(128, 128, 80, 40), 2)),
        ["square"] = P(S(Ink, Rect(56, 56, 144, 144), 2)),
        ["triangle"] = P(S(Ink, Poly(128, 40, 216, 204, 40, 204, 128, 40), 2)),
        ["arrow"] = P(S(Ink, Poly(40, 128, 200, 128), 2), S(Ink, Poly(160, 88, 206, 128, 160, 168), 2)),
        ["smiley"] = P(S(Orange, Circle(128, 128, 84, 40), 2), S(Ink, Circle(100, 104, 8, 10)), S(Ink, Circle(156, 104, 8, 10)), S(Ink, Arc(128, 132, 48, 20, 160), 2)),
        ["question mark"] = P(S(Ink, Arc(128, 90, 42, 190, 400).Concat(Poly(128, 150, 128, 170)), 3), S(Ink, Circle(128, 205, 6, 10), 3)),
        ["musical note"] = P(S(Ink, Circle(98, 190, 22), 3), S(Ink, Poly(120, 190, 120, 50, 176, 72), 3)),
        ["flag"] = P(S(Brown, Poly(70, 236, 70, 36), 3), S(Red, Poly(70, 40, 200, 70, 70, 110), 2)),
        ["diamond"] = P(S(Blue, Poly(128, 40, 206, 128, 128, 216, 50, 128, 128, 40), 2)),
        ["spiral"] = P(S(Ink, Enumerable.Range(0, 120).Select(i =>
        {
            double a = i * 0.16, r = 4 + i * 0.75;
            return (128 + r * Math.Cos(a), 128 + r * Math.Sin(a));
        }), 2)),
        ["cube"] = P(S(Ink, Rect(60, 100, 100, 100), 2), S(Ink, Poly(60, 100, 100, 60, 200, 60, 160, 100), 2), S(Ink, Poly(200, 60, 200, 160, 160, 200), 2)),
        ["apple"] = P(S(Red, Arc(104, 142, 50, 70, 290).Concat(Arc(152, 142, 50, 250, 470)), 2), S(Brown, Poly(128, 96, 136, 60), 2), S(Green, Arc(152, 66, 18, 180, 360))),
        ["cherry"] = P(S(Red, Circle(92, 184, 28), 2), S(Red, Circle(166, 176, 28), 2), S(Green, Poly(92, 156, 136, 60, 166, 148), 2)),
        ["fish"] = P(S(Blue, Arc(120, 128, 60, 30, 330, 28).Concat(Poly(214, 88, 214, 168, 172, 98)), 2), S(Ink, Circle(92, 116, 6, 10))),
        ["bug"] = P(S(Red, Circle(128, 142, 60), 2), S(Ink, Circle(128, 76, 22), 2), S(Ink, Poly(128, 82, 128, 202)),
            S(Ink, Poly(72, 120, 40, 100)), S(Ink, Poly(184, 120, 216, 100)), S(Ink, Poly(70, 150, 36, 150)), S(Ink, Poly(186, 150, 220, 150)), S(Ink, Poly(76, 180, 46, 204)), S(Ink, Poly(180, 180, 210, 204))),
        ["key"] = P(S(Orange, Circle(74, 128, 32), 3), S(Orange, Poly(106, 128, 220, 128, 220, 156), 3), S(Orange, Poly(186, 128, 186, 152), 3)),
        ["lock"] = P(S(Orange, Rect(68, 118, 120, 100), 3), S(Ink, Arc(128, 118, 40, 180, 360), 3), S(Ink, Poly(128, 150, 128, 182), 3)),
        ["envelope"] = P(S(Ink, Rect(40, 72, 176, 118), 2), S(Ink, Poly(40, 72, 128, 138, 216, 72), 2)),
        ["mug"] = P(S(Brown, Poly(66, 72, 66, 204, 170, 204, 170, 72), 2), S(Brown, Arc(170, 138, 30, 270, 450), 2), S(Ink, Poly(96, 58, 90, 40)), S(Ink, Poly(124, 58, 118, 36))),
        ["clock"] = P(S(Ink, Circle(128, 128, 88, 40), 2), S(Ink, Poly(128, 128, 128, 70), 3), S(Ink, Poly(128, 128, 170, 150), 2)),
        ["umbrella"] = P(S(Red, Arc(128, 124, 90, 180, 360, 24).Concat(Poly(38, 124, 218, 124)), 2), S(Ink, Poly(128, 124, 128, 204).Concat(Arc(112, 204, 16, 0, 180)), 2)),
        ["ball"] = P(S(Red, Circle(128, 128, 80, 40), 2), S(Ink, Arc(128, 20, 118, 60, 120)), S(Ink, Arc(128, 236, 118, 240, 300))),
        ["balloon"] = P(S(Red, Circle(128, 100, 58), 2), S(Red, Poly(120, 158, 136, 158, 128, 168)), S(Ink, Poly(128, 168, 118, 196, 138, 220, 124, 246))),
        ["kite"] = P(S(Blue, Poly(128, 30, 190, 110, 128, 190, 66, 110, 128, 30), 2), S(Blue, Poly(128, 30, 128, 190)), S(Blue, Poly(66, 110, 190, 110)),
            S(Ink, Poly(128, 190, 110, 214, 140, 232, 120, 250))),
        ["ladder"] = P(S(Brown, Poly(88, 30, 88, 236), 3), S(Brown, Poly(168, 30, 168, 236), 3),
            S(Brown, Poly(88, 60, 168, 60)), S(Brown, Poly(88, 100, 168, 100)), S(Brown, Poly(88, 140, 168, 140)), S(Brown, Poly(88, 180, 168, 180)), S(Brown, Poly(88, 220, 168, 220))),
        ["candle"] = P(S(Red, Rect(104, 110, 48, 120), 2), S(Orange, Arc(128, 84, 16, 20, 340).Concat(Poly(128, 50)).Concat(Poly(143, 78)), 2)),
        ["glasses"] = P(S(Ink, Circle(82, 130, 34), 2), S(Ink, Circle(174, 130, 34), 2), S(Ink, Arc(128, 132, 12, 200, 340), 2), S(Ink, Poly(48, 124, 26, 110))),
        ["eye"] = P(S(Ink, Arc(128, 190, 110, 225, 315, 20).Concat(Arc(128, 66, 110, 45, 135, 20)), 2), S(Blue, Circle(128, 128, 26), 2), S(Ink, Circle(128, 128, 9, 10), 3)),
        ["hat"] = P(S(Ink, Poly(40, 180, 216, 180), 3), S(Ink, Poly(76, 180, 86, 80, 170, 80, 180, 180), 2), S(Red, Poly(84, 150, 172, 150), 2)),
        ["bell"] = P(S(Orange, Poly(70, 180, 80, 104).Concat(Arc(128, 104, 48, 180, 360)).Concat(Poly(176, 104, 186, 180, 70, 180)), 2), S(Orange, Circle(128, 196, 12))),
        ["mountain"] = P(S(Brown, Poly(20, 210, 96, 70, 140, 150, 176, 100, 236, 210, 20, 210), 2), S(Ink, Poly(76, 106, 96, 118, 116, 106))),
        ["wave"] = P(S(Blue, Enumerable.Range(0, 60).Select(i => (20.0 + i * 3.6, 128 + 28 * Math.Sin(i * 0.3))), 3), S(Blue, Enumerable.Range(0, 60).Select(i => (20.0 + i * 3.6, 176 + 20 * Math.Sin(i * 0.3 + 1))), 2)),
        ["lightning"] = P(S(Orange, Poly(150, 26, 90, 136, 134, 136, 104, 234, 176, 110, 132, 110, 150, 26), 2)),
        ["rocket"] = P(S(Ink, Poly(128, 26, 164, 90, 164, 190, 92, 190, 92, 90, 128, 26), 2), S(Blue, Circle(128, 110, 16)), S(Ink, Poly(92, 150, 66, 204, 92, 190)), S(Ink, Poly(164, 150, 190, 204, 164, 190)),
            S(Orange, Poly(104, 196, 128, 244, 152, 196), 2)),
        ["sailing boat"] = P(S(Brown, Poly(46, 170, 210, 170, 180, 214, 76, 214, 46, 170), 2), S(Ink, Poly(128, 170, 128, 40), 2), S(Red, Poly(128, 46, 196, 150, 128, 150))),
        ["car"] = P(S(Red, Poly(30, 170, 30, 130, 76, 124, 100, 90, 170, 90, 196, 124, 226, 130, 226, 170, 30, 170), 2), S(Ink, Circle(78, 176, 20), 2), S(Ink, Circle(182, 176, 20), 2)),
        ["cake"] = P(S(Brown, Rect(52, 130, 152, 86), 2), S(Red, Poly(52, 150, 204, 150), 2), S(Orange, Poly(128, 130, 128, 90), 3), S(Orange, Circle(128, 80, 8, 10))),
        ["snowflake"] = P(S(Blue, Poly(128, 30, 128, 226), 2), S(Blue, Poly(43, 79, 213, 177), 2), S(Blue, Poly(43, 177, 213, 79), 2)),
        ["tent"] = P(S(Green, Poly(24, 210, 128, 50, 232, 210, 24, 210), 2), S(Ink, Poly(128, 50, 104, 210)), S(Ink, Poly(128, 50, 152, 210))),
        ["cactus"] = P(S(Green, Poly(110, 226, 110, 60).Concat(Arc(128, 60, 18, 180, 360)).Concat(Poly(146, 226)), 2), S(Green, Poly(110, 150, 76, 150, 76, 100), 2), S(Green, Poly(146, 130, 180, 130, 180, 86), 2)),
    };
}
