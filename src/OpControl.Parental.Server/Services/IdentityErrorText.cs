// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.AspNetCore.Identity;

namespace OpControl.Parental.Server.Services;

public static class IdentityErrorText
{
    public static string Describe(IEnumerable<IdentityError> errors) =>
        string.Join(" ", errors.Select(e => Describe(e)));

    public static string Describe(IdentityError error) => error.Code switch
    {
        "PasswordTooShort" => AppText.Get("Identity.PasswordTooShort"),
        "PasswordRequiresDigit" => AppText.Get("Identity.PasswordRequiresDigit"),
        "PasswordRequiresUpper" => AppText.Get("Identity.PasswordRequiresUpper"),
        "PasswordRequiresLower" => AppText.Get("Identity.PasswordRequiresLower"),
        "PasswordRequiresNonAlphanumeric" => AppText.Get("Identity.PasswordRequiresNonAlphanumeric"),
        "PasswordMismatch" => AppText.Get("Identity.PasswordMismatch"),
        "DuplicateUserName" or "DuplicateEmail" => AppText.Get("Identity.DuplicateEmail"),
        "InvalidEmail" or "InvalidUserName" => AppText.Get("Identity.InvalidEmail"),
        _ => AppText.Get("Identity.Default")
    };
}
