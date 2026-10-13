# Fixtures

Anonymized real-world iCalendar resources, one quirk each. Add new ones as
`<provider-or-quirk>.ics` and reference them from `tests/Termin.Unit`.

| File | Source style | What it exercises |
|---|---|---|
| `weekly-dst-override.ics` | Apple | Weekly series across the Europe/Zurich DST switch (25 Oct 2026), EXDATE, an override moved to another day, X- properties, VALARM |
| `allday-multiday.ics` | Nextcloud | All-day DATE with exclusive DTEND, TRANSP |
| `until-utc.ics` | Google | RRULE with UTC UNTIL against a TZID start |
| `floating.ics` | Radicale | Floating times (no TZID) |
