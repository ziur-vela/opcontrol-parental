// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OpControl.Parental.Server.Pages;

public class ThirdPartyNoticesModel : PageModel
{
    public string NoticesText { get; private set; } =
        "Third-party notices are distributed with Parental OpControl. See THIRD-PARTY-NOTICES.md in the repository.";

    public void OnGet() => NoticesText = LicenseModel.ReadNotice("THIRD-PARTY-NOTICES.md");
}
