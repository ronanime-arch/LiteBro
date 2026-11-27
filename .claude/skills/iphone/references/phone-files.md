# Где что лежит на телефоне

Корень общего доступа в `LiteBro.tipa` — `/var/mobile` (пользовательская часть iOS). Всё ниже — пути от этого корня. В `LiteBro-Browser.tipa` корень — папка `Documents` самого LiteBro, и из этого файла там есть только её содержимое (`Downloads/`, `Debug/`).

Раскладка — обычная для iOS 16; на телефоне проверь `ls`, прежде чем на неё опираться. Системные файлы вне `/var/mobile` (сами приложения в `/var/containers/Bundle`, связка ключей в `/var/Keychains`) недоступны.

## Быстрый поиск

| Нужно | Где |
|---|---|
| Фото и видео камеры | `Media/DCIM/1xxAPPLE/` (`IMG_….HEIC`, `.JPG`, `.MOV`, `.PNG`); правки — `Media/PhotoData/Mutations/` |
| База медиатеки (альбомы, даты, лица) | `Media/PhotoData/Photos.sqlite` — большая, личная; не копируй без просьбы |
| Загрузки Safari и «Файлы → На iPhone» | `Media/Downloads/`; у каждого приложения с папкой в «Файлах» — его `Documents/` |
| Данные приложения по имени | `iphone apps <часть bundle id>` → `Containers/Data/Application/<UUID>/` |
| Общие папки приложений (мессенджеры, расширения) | `iphone apps <часть> --groups` → `Containers/Shared/AppGroup/<UUID>/` |
| Журналы падений всех приложений | `Library/Logs/CrashReporter/*.ips` (и `JetsamEvent-*.ips` — убито из-за памяти) |
| Журнал LiteBro | `Media/LiteBro-Debug/log.txt` (`iphone log`) |
| iCloud Drive (локальная копия) | `Library/Mobile Documents/` |

## Папка LiteBro

`iphone apps litebro` или строка `data …` в `iphone log --launch` дают `Containers/Data/Application/<UUID>`. Внутри:

| Путь | Что |
|---|---|
| `Documents/Downloads/` | скачанное браузером (видео, файлы) |
| `Documents/Debug/log.txt`, `crash.txt`, `launching.txt`, `busy-*.txt` | журнал запуска; `launching.txt` остался — прошлый запуск не дошёл до конца; `busy-<работа>.txt` — приложение умерло в этой работе и пропускает её |
| `Library/Preferences/io.github.ronanime-arch.LiteBro.plist` | настройки и сессия: `tabs` (адреса вкладок, пустая строка — плитки), `currentTab`, `theme`, `adBlock`, `localOnly`, `allowHosts`, `unloadAfter`, `search`, `protect`, `console` |
| `Library/Application Support/tiles.json` | плитки: имя, адрес, ссылки, цвет |
| `Library/Application Support/thumbs/` | картинки сохранённых страниц |
| `Library/Application Support/ads-*.json` и рядом | списки блокировщика, уже переведённые в правила |
| `Library/WebKit/`, `Library/Cookies/` | данные сайтов (входы) |

Общая папка приложения и расширения экрана — группа `group.io.github.ronanime-arch.LiteBro` (`iphone apps litebro --groups`): там срок и секрет показа экрана.

## Форматы

- `.plist` — чаще двоичный: `iphone plist <путь>` печатает JSON.
- `.sqlite`, `.db`, `.sqlitedb`, `.storedata` — SQLite: скачай (`get`) вместе с файлами `-wal` и `-shm` рядом и открывай на компьютере (`sqlite3`, Python `sqlite3`). Не пиши в базы на телефоне.
- `.ips` — текст (заголовок JSON и отчёт): `iphone cat`.
- `.HEIC`, `.MOV` — как есть; на Windows HEIC открывается не везде, для просмотра сконвертируй на компьютере.

## Что можно менять, а что нет

- Свои файлы пользователя (`Media/Downloads`, `Documents` приложений) — можно, по его просьбе.
- Настройки и базы работающего приложения менять бесполезно и опасно: iOS держит настройки в памяти и перезапишет файл, а база может повредиться. Если нужно изменить состояние LiteBro, делай это через его экран (касания) или новой сборкой.
- `Media/DCIM` и `PhotoData`: удаление файла мимо «Фото» оставляет в медиатеке битую запись. Удалять фото пусть будет в «Фото»; копировать с телефона — можно.
- Удаление необратимо: корзины нет.

## Личное

Здесь же лежат сообщения (`Library/SMS/sms.db`), контакты (`Library/AddressBook/`), история звонков (`Library/CallHistoryDB/`), история Safari (`Library/Safari/`), почта, заметки, здоровье. Это данные не только пользователя, но и его собеседников. Открывай их, только если задача прямо об этом; пересказывай нужное, а не выгружай целиком; с телефона на компьютер и тем более наружу копируй только то, что пользователь назвал.
