#!/bin/sh
# Copyright (c) 2026 Ziur / OpControl IT Solutions
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

set -e
mkdir -p /var/lib/opcontrol-parental/keys /var/lib/opcontrol-parental/releases
chown -R appuser:appuser /var/lib/opcontrol-parental/keys /var/lib/opcontrol-parental/releases 2>/dev/null || true
exec setpriv --reuid=10001 --regid=10001 --clear-groups dotnet OpControl.Parental.Server.dll "$@"
