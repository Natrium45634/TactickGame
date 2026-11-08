# Приёмка ТЗ ч.5 (§11): где проверен каждый пункт

Состояние на **v0.25.0**. «Тест» — автотест `dotnet test` (класс.метод), «Сценарий» — регрессия Windows-сборки
`tools/verify_release.ps1` (`$RegressionScenarioV2x`, второй запуск `$PersistenceScenarioV2x`, чистая установка
`$CleanImportScenarioV2x`). Все сценарии запускаются в каждой следующей версии — прежние пункты перепроверяются.

| № | Пункт (кратко) | Тест | Сценарий |
|---|---|---|---|
| 1 | «Механики» в «Разработчике», нет в игре | RegistryTests (группа `mechanics`) | V23: экран «Механики»; в «Играть» каталога нет |
| 2 | встроенные типы; свои — через реестр | BuiltInContentTests, MechanicsTests.P35 | V23: «Плазма» |
| 3 | «Плазменный» без кода, во всех редакторах, переименование без смены ID | MechanicsTests.P35, Part5EndToEndTests.P3_P7 | V23 |
| 4 | одиночный и площадной колющий — одна формула и тип | DamageResolverTests.P4 | — |
| 5 | смешанный удар 12 + 8 | DamageResolverTests.P5 | — |
| 6 | зона навыка → цели на момент удара, без дублей | DamageResolverTests (разделение силы), SkillEffectsTests | V18 |
| 7 | встроенные типы не меняются и не удаляются | Part5EndToEndTests.P3_P7 | — |
| 8 | таблица R для D = 40 | DamageResolverTests.P8 | V21 |
| 9 | R = 300 % без правила: 0 / 0 / 0 | DefenseRulesTests.Reference_table, MechanicsTests.P38 | V23: «без правила 60 → 60» |
| 10 | отказ вне ±500, NaN, цикл наследования | DamageContentTests.P10, ProfileContentTests, MechanicsTests (наборы) | V21: отказ 600 %; V23: отказ 700 % |
| 11 | 10 + 20 − 15 = 15 | DamageResolverTests.P11 | V21 |
| 12 | подсказки −50/+75/+300 % | UnitEditorTests (Explain) | V21 |
| 13 | пробитие; иммунитет не пробивается | DamageResolverTests.P13, Part5AcceptanceTests.P13 | V23: пробитие 50 |
| 14 | чистый мимо R; иммунитет; щит | DamageResolverTests.P14, DefenseRulesTests (таблица, P25) | V24: «Особые правила» у чистого |
| 15 | временное R, пауза | DamageResolverTests.P15 | — |
| 16 | иммунитет к подтипу/семейству | DefenseRulesTests.P16 | — |
| 17 | иммунитет и контактная конверсия | DefenseRulesTests (таблица §5.4) | — |
| 18 | огонь 300 %: 60 → 70, по 30 соседям | DefenseRulesTests.P18, DefenseContentTests.P18 | V22: бой; V23: песочница |
| 19 | 98/100: лечение 2, отражение 30 | DefenseRulesTests.P19 | V22: 90 → 100, по 30 |
| 20 | без врагов доля исчезает | DefenseRulesTests.P20 | — |
| 21 | конверсии ≤ 100 % | DefenseRulesTests.P21, DefenseContentTests | V22: отказ 900 % |
| 22 | ответ 50 % от 20 = 10 | DefenseRulesTests (таблица) | — |
| 23 | два «зеркала» без цепи | DefenseRulesTests.P23 | — |
| 24 | +50 %, снижение 5, щит 12 → HP −3, щит −12 | DefenseRulesTests (таблица) | — |
| 25 | щит на нескольких ударах, чистый урон | DefenseRulesTests.P25 | — |
| 26 | без случайностей, воспроизводимо | Part5AcceptanceTests.P34 (повтор) | — |
| 27 | вампиризм только от снятого HP | DefenseRulesTests.P27 | — |
| 28 | реакции не зависят от FPS, стоят на паузе | DamageResolverTests.P26_P28, Part5AcceptanceTests.P34, .P28 | — |
| 29 | навык в ТД с типом через расчёт, маршрут дальше | Part5AcceptanceTests.P29 | V14, V18 |
| 30 | 3 × 150 % и 3 × 75 % с разными типами | Part5AcceptanceTests.P30 | V18 |
| 31 | поверхность 5 HP/с: 10, при +50 % — 5; старая — как раньше | DamageResolverTests.P31, DamageContentTests.P44 | V19 |
| 32 | реакция не меняет ТД, маршрут, взгляд | Part5AcceptanceTests.P32 | — |
| 33 | смерть от отражения один раз | DefenseRulesTests.P33 | V22: атакующий 100 → 40 |
| 34 | пять юнитов с разной защитой | Part5AcceptanceTests.P34 | V25: бой пяти юнитов |
| 35 | R не видны в игре; в тесте — отладка | — | V21/V22/V24 `expect:info`, V24 `game.debugdefense`, `expect:control` |
| 36 | юнит HP 100 + физ. +50 % без «Механик» | UnitEditorTests.P36 | V24: «Танк v24»; V25: UX-1 |
| 37 | сложная огненная защита через UI, резюме до сохранения | DefenseRuleEditorTests | V22, V25: UX-4 |
| 38 | короткий список; поиск находит нужное | ResistanceUxTests | V24: поиск «Плазма» |
| 39 | набор на двух юнитах; локальное изменение | ResistanceUxTests.P39 | V23: «Латник» |
| 40 | изменение правила — после сохранения, не в тике | Part5EndToEndTests.P40 | — |
| 41 | экспорт/импорт с тем же ударом | Part5EndToEndTests.P41_P43, DefenseContentTests.P46, ProfileContentTests | V25 чистая установка: «Защита» |
| 42 | неизвестный обработчик / нет зависимости | Part5EndToEndTests.P42 | V20: файл отклонён |
| 43 | перезапуск восстанавливает типы и правила | Part5EndToEndTests.P41_P43 | второй запуск V21–V25 |
| 44 | контент ч.3–4 после миграции с прежними числами | DamageContentTests.P44, Part5EndToEndTests.P44, DefenseContentTests, ProfileContentTests | чистая установка V20 (старый пакет) |
| 45 | «Проверить на ударе» — все стадии и цели | MechanicsTests.P38 | V23 |
| 46 | предпросмотр не меняет библиотеку и матч | MechanicsTests.P38 (`ContentLibrary.Version`) | — |
| 47 | новое поле правила — везде сразу | Part5EndToEndTests.P47 | — |
| 48 | регрессия ч.1–4 | все прежние тесты | V05–V25 подряд |
