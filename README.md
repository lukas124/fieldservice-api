# FieldService API (szkoleniowe)

Backend aplikacji **FieldService** (.NET MAUI) ze szkolenia „Architektura aplikacji w .NET MAUI”: ASP.NET Core
(net9.0), PostgreSQL (EF Core), JWT, Swagger, OpenTelemetry.

- Publicznie: **https://mobilny.dev/maui/architektura/** – strona startowa z instrukcją, Swagger `/swagger`,
  kontrakt `/swagger/v1/swagger.json`, health `/api/health`.

## Swagger – jak zacząć

1. `POST /api/auth/login` → „Try it out” → e-mail i PIN `1234` → Execute → skopiuj `accessToken`.
2. **Authorize** (góra strony) → wklej sam token (bez słowa Bearer). Autoryzacja zostaje po odświeżeniu strony.
3. `GET /api/visits/today` → `version` wybranej wizyty.
4. Komenda, np. `POST /api/visits/{id}/start`: wersja w polu **`X-If-Match`** (proxy po drodze może wyciąć `If-Match`)
   i dowolny `Idempotency-Key`. Ten sam klucz drugi raz → ta sama odpowiedź (`Idempotent-Replayed: true`), stara wersja → 409.
5. Dyspozytor (sekcja **Dispatcher**, bez logowania): pole `X-Dispatcher-Key` – na szkoleniu klucz ma tylko trener.

Grupy (tagi) i wyliczenia mają opisy, operacje – czytelne `operationId` (`GetTodayVisits`, `ReservePart`, …) – z nich
generator klienta (NSwag w aplikacji) bierze nazwy metod.
- Logowanie: dowolny poprawny e-mail + PIN `1234`. Każdy e-mail to osobny technik z własnymi danymi.
- Dane są demonstracyjne i fikcyjne (klienci, adresy, telefony).

## Dzień pracy technika

Przy pierwszym logowaniu danego dnia serwer tworzy dzień pracy technika:

- **4 wizyty na dziś**: 8:00 (już rozpoczęta), 10:00, 12:30 i 14:30. Każda ma zlecenie, klienta, urządzenie i opis usterki.
- **Auto technika**: 6 części.
- **Historia**: 20 zakończonych wizyt z poprzednich dni. Tylko przy pierwszym logowaniu.

Konta specjalne (`Demo:*` w `appsettings.json`):

| Konto | Po co |
|---|---|
| `technik@szkolenie.pl`, `technik2@szkolenie.pl` | brygada: wizyta 12:30 jest wspólna (dwa telefony, konflikt wersji) |
| `technik2@szkolenie.pl` | trzyma rezerwację ostatniej sztuki `KOMPRESOR-R32` w `MAG-01` – rezerwacja tej części zawsze kończy się 409 |
| `wydajnosc@szkolenie.pl` | 2000 wizyt w historii (płynna lista, cache miniatur) |

Katalog części (12 pozycji) i dwa magazyny (`MAG-01`, `MAG-02`) są wspólne dla wszystkich.

## Kontrakt

Wszystko pod `api/`. Wymaga `Authorization: Bearer <accessToken>`, poza auth, health, images i dispatcher.

| Endpoint | Opis |
|---|---|
| `POST auth/login`, `POST auth/refresh` | tokeny; refresh token jednorazowy (rotacja) |
| `GET visits/today` | wizyty na dziś (`ETag` → `If-None-Match` → 304) |
| `GET visits/history?page&pageSize` | zakończone i anulowane z poprzednich dni, strony po maks. 100 |
| `GET visits/{id}` | szczegóły; `ETag` = wersja wizyty |
| `POST visits/{id}/start` | R1: tylko `Planned` |
| `POST visits/{id}/activities` | R2: czynność (`InProgress`) |
| `POST visits/{id}/parts`, `DELETE visits/{id}/parts/{partId}` | R2/R7: zużyta część, usunięcie omyłkowej |
| `PUT visits/{id}/signature` | R4: podpis pod bieżącym zakresem (niepustym) |
| `POST visits/{id}/complete` | R5: `Fixed` wymaga ważnego podpisu i zamyka zlecenie; `NeedsFollowUp`/`NotFixed` wymagają powodu |
| `GET workorders` | „Moje zlecenia” |
| `GET parts`, `GET warehouses`, `GET parts/{id}/stock`, `GET vanstock` | katalog (`ETag`), magazyny, stan, auto technika |
| `POST parts/reservations`, `GET parts/reservations`, `DELETE parts/reservations/{id}` | rezerwacje (wygasają po 30 min) |
| `GET images/{key}[?size=thumb]` | PNG 800 px / 160 px, bez logowania, `Cache-Control: immutable` |
| `api/dispatcher/…` | symulacja dyspozytora: lista wizyt, anulowanie (R6), przeniesienie, nowa wizyta, stan magazynu |
| `POST admin/reset` | reset (nagłówek `X-Admin-Key`) |

**Reguły wizyty (R1–R7):** serwer ma własny model wizyty (`Domain/Visit.cs`) z tymi samymi regułami co aplikacja. Kontraktem między nimi jest OpenAPI, nie wspólny kod. Zmiana zakresu prac po podpisie unieważnia podpis (R3): podpis wskazuje starszą wersję zakresu (`signature.isValid = false`).

**Komendy wizyty** obsługują dwa nagłówki:

- `If-Match: "<wersja>"` – **wymagany**. To wersja, którą widzi aplikacja: z `version` w DTO albo z nagłówka `ETag`.
  - `X-If-Match` z tą samą wartością działa tak samo. Proxy po drodze może wyciąć `If-Match`, więc aplikacja
    wysyła `X-If-Match`. Gdy przyjdą oba, wygrywa `If-Match`.
  - Brak nagłówka → 428 `Visit.PreconditionRequired`.
  - Inna wersja → 409 `Visit.VersionConflict` z polami `currentVersion` i `currentStatus`.
  - Każda udana komenda podnosi wersję o 1.
  - Zmiany dyspozytora też podnoszą wersję, więc telefon ze starą wersją dostaje 409 (kompensacja po stronie aplikacji).
- `Idempotency-Key` – zalecany. Aplikacja nadaje go raz, przy zapisie w outboxie.
  - Powtórka z tym samym kluczem i tą samą treścią zwraca zapisaną odpowiedź z nagłówkiem `Idempotent-Replayed: true`, bez drugiego wykonania. Działa nawet wtedy, gdy wersja wizyty jest już nowsza.
  - Ten sam klucz z inną treścią → 422 `Idempotency.KeyReused`.
  - Klucze są pamiętane 24 h.

**Zapas części (`Domain/Inventory.cs`)** to agregat dla jednej części w jednym magazynie.
- Reguła: suma aktywnych rezerwacji ≤ stan. Rezerwacje wygasają po czasie (TTL), bez zadania w tle.
- Dwie równoczesne rezerwacje ostatniej sztuki: zapisze się jedna, druga trafi na konflikt wersji, sprawdzi regułę jeszcze raz i dostanie 409 `Inventory.OutOfStock` (z polem `available`).

**Błędy** to ProblemDetails z polem `code`:

| Kod | Kody błędów |
|---|---|
| 404 | `Visit.NotFound`, `Inventory.PartNotFound`, `Inventory.StockNotFound`, `Inventory.ReservationNotFound` |
| 409 | `Visit.VersionConflict`, `Visit.InvalidTransition`, `Visit.NotInProgress`, `Visit.AlreadyCompleted`, `Inventory.OutOfStock`, `Idempotency.InProgress` |
| 422 | `Visit.EmptyScope`, `Visit.SignatureRequired`, `Visit.ReasonRequired`, `Visit.PartNotInScope`, `Visit.InvalidQuantity`, `Visit.Validation`, `Inventory.InvalidQuantity`, `Idempotency.KeyReused` |
| 428 | `Visit.PreconditionRequired` |

Wyliczenia w JSON są tekstowe, daty w ISO 8601 (UTC). „Dziś” liczy się w strefie `Demo:TimeZone` (Europe/Warsaw).

## Baza danych

PostgreSQL przez EF Core (Npgsql). Schemat tworzy `Database.EnsureCreated()` przy starcie, **bez migracji**. Przy zmianie modelu usuwa się bazę, a API zakłada ją od nowa.

Każda jednostka spójności to jeden wiersz, więc zapis jest „całość albo nic”:
- zakres prac wizyty (czynności, części) i podpis są kolumnami JSON w wierszu `Visits`;
- rezerwacje są kolumną JSON w wierszu `PartStocks`.

`Version` to token współbieżności (blokowanie optymistyczne).

Connection string tylko ze środowiska: `ConnectionStrings__FieldService`. Lokalnie ustawia go `Properties/launchSettings.json`, na serwerze plik env.

## Uruchomienie lokalne

```bash
# jednorazowo: PostgreSQL i rola deweloperska
brew install postgresql@17 && brew services start postgresql@17
psql -d postgres -c "CREATE ROLE fieldservice LOGIN PASSWORD 'fieldservice' CREATEDB;"

dotnet run --project src/FieldService.Api                               # http://localhost:5090, Admin key: dev-admin-key
dotnet run --project src/FieldService.Api --launch-profile siec-sali    # 0.0.0.0:5090 — telefony w sieci sali
dotnet run --project src/FieldService.Api --launch-profile telemetria   # OTLP do http://localhost:4318 (grafana/otel-lgtm)
```

Emulator Android łączy się z API pod adresem `http://10.0.2.2:5090`.

Konfiguracja: plik `appsettings.json`, nadpisywany zmiennymi `Sekcja__Klucz`. Sekcje:

| Klucz | Co ustawia |
|---|---|
| `PathBase`, `RedirectOutsidePathBase` | prefiks ścieżki za proxy; przekierowanie żądań spoza niego |
| `Hosting:*` | port i nasłuch na wszystkich interfejsach |
| `Auth:*` | PIN i czasy życia tokenów |
| `Demo:*` | strefa czasowa, historia, konta specjalne, opóźnienie list do pomiarów cache (`ListDelayMilliseconds`) |
| `Inventory:ReservationMinutes` | czas życia rezerwacji |
| `Admin:Key`, `Dispatcher:Key` | klucze resetu i dyspozytora |
| `RateLimiting:*` | limity żądań na adres IP |
| `OTEL_EXPORTER_OTLP_*` | eksport telemetrii (bez adresu – brak eksportu) |

## Telemetria

Gdy ustawiono `OTEL_EXPORTER_OTLP_ENDPOINT`, API wysyła przez OTLP ślady, metryki i logi:
- **ślady**: ASP.NET Core, HttpClient, Npgsql (zapytania SQL);
- **metryki**: ASP.NET Core, HttpClient;
- **logi**.

Zasób to `service.name = fieldservice-api` i `service.version`. Ślad z telefonu (`traceparent`) ciągnie się przez kontroler aż do zapytania SQL. Health nie jest śledzony. `GET api/health` pokazuje `telemetryExport`.

## Testy

```bash
dotnet test                                   # NUnit + WebApplicationFactory na lokalnym PostgreSQL (każda klasa: własna baza)
                                              # inny serwer: FIELDSERVICE_TEST_DB="Host=…;Username=…;Password=…"
scripts/smoke.sh http://localhost:5090        # smoke przez curl na działającym API
ADMIN_KEY=... scripts/smoke.sh https://mobilny.dev/maui/architektura   # + sprzątanie po sobie
```
