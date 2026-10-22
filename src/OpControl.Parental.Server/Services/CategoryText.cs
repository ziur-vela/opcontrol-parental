// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

public static class CategoryText
{
    public static string Name(string key, string fallback, string? language = null) => key switch
    {
        "adult" => AppText.Get("Category.Adult", language),
        "gambling" => AppText.Get("Category.Gambling", language),
        "social" => AppText.Get("Category.Social", language),
        "fakenews" => AppText.Get("Category.FakeNews", language),
        _ => fallback
    };
}
