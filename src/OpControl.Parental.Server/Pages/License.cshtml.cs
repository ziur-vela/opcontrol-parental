// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OpControl.Parental.Server.Pages;

public class LicenseModel : PageModel
{
    private const string Fallback =
        "Required Notice: Copyright (c) 2026 Ziur / OpControl IT Solutions (https://opcontrol.net)\n\n" +
        "PolyForm Noncommercial License 1.0.0\nhttps://polyformproject.org/licenses/noncommercial/1.0.0";

    public static string ReadNotice(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : Fallback;
    }

    public string LicenseText { get; private set; } = Fallback;

    public void OnGet() => LicenseText = ReadNotice("LICENSE");
}
