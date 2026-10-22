// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Contracts;

/// <summary>
/// Pure geometry rules for the child and parent chat threads. The child bubble
/// is pinned to the right edge and the parent bubble to the left, and every row
/// spans the full thread width so short and long messages share the same edge.
/// </summary>
public static class ChatLayout
{
    public const double MaxBubbleFraction = 0.62;
    public const int MinimumRowWidth = 260;
    public const int MinimumBubbleWidth = 180;

    public static int RowWidth(int clientWidth, int horizontalPadding, int reserved = 14) =>
        Math.Max(MinimumRowWidth, clientWidth - horizontalPadding - reserved);

    public static int MaxBubbleWidth(int rowWidth) =>
        Math.Max(MinimumBubbleWidth, (int)(Math.Max(MinimumRowWidth, rowWidth) * MaxBubbleFraction));

    /// <summary>0 = left (parent), 1 = right (child).</summary>
    public static int ColumnFor(bool own) => own ? 1 : 0;

    public static float FirstColumnPercent(bool own) => own ? 38f : 62f;

    public static float SecondColumnPercent(bool own) => own ? 62f : 38f;
}
