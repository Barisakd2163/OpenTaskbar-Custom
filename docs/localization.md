# Localization

OpenTaskbar stores the selected UI language as a BCP 47 culture tag, such as `tr-TR` or `en-US`. On first launch, it selects the supported translation matching the Windows display language. Regional variants such as `en-GB` keep their culture-specific formatting while using the matching English translation. Existing settings that contain `Turkish` or `English` are still read and are saved using culture tags next time.

## Adding a language

1. Add an `AppLanguage` value with its culture tag in `Models/DockSettings.cs`, then include it in `AppLanguage.Supported`.
2. Add the translation for every UI key in `Services/Localization/UiText.cs`. Use `LocalizedEntry.Create` when an entry needs more than the existing Turkish and English values.
3. Check all new text in a narrow Settings window and in the dock widgets. The language picker uses each culture's native display name.

Translations that are not available use the English catalog entry. Keep dates, numbers, percentages, and localized service queries culture-based; do not add language-specific conditionals for individual cultures.

## Existing fixed text

User-facing labels and errors belong in the `UiText` catalog. Product names, keyboard shortcuts, file formats, protocol values, operating-system identifiers, and source data such as city names are not translated UI copy.
