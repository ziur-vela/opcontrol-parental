// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace OpControl.Parental.Server.Services;

public sealed class EmailOptions
{
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    public string? LogoUrl { get; set; }
    public bool Staging { get; set; }
}
