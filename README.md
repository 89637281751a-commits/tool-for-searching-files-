# QuickFind

Поиск файлов для Windows 10 и 11. Дважды нажмите Ctrl, введите часть имени и нажмите Enter, чтобы открыть выбранный файл. Пустой запрос показывает до десяти недавних файлов.

**[Скачать QuickFind 0.1.0 для Windows (.msi)](https://raw.githubusercontent.com/89637281751a-commits/tool-for-searching-files-/e4156f794321bdc59ea714e2fa550c846e616140/releases/v0.1.0/QuickFind-0.1.0.msi)**

[История обновлений](CHANGELOG.md) · [Предварительный выпуск 0.1.0](https://github.com/89637281751a-commits/tool-for-searching-files-/releases/tag/v0.1.0) · [Контрольная сумма SHA-256](https://raw.githubusercontent.com/89637281751a-commits/tool-for-searching-files-/e4156f794321bdc59ea714e2fa550c846e616140/releases/v0.1.0/SHA256SUMS.txt)

## Установка

1. Если QuickFind уже запущен, выберите **Выход** в меню значка программы рядом с часами.
2. Откройте скачанный файл `QuickFind-0.1.0.msi`.
3. После установки запустите **QuickFind** из меню **Пуск**. Чтобы открыть поиск, дважды нажмите Ctrl.

Запуск QuickFind при входе в Windows включается в меню значка рядом с часами. При обновлении история и индекс сохраняются. Для удаления используйте **Параметры → Приложения**. [Подробная инструкция](Readme.txt)

Установщик предварительной версии без цифровой подписи. Windows может показать предупреждение о неизвестном издателе.

## Сборка MSI

На Linux с .NET SDK 8, Python 3 и wixl 0.103 или новее выполните `bash installer/build.sh`. MSI появится в `bin/installer/<версия>/`. Версия релиза задаётся в `QuickFind.csproj`.
