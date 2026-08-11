namespace BeeMemoryBank.SeedGen;

/// <summary>
/// Curated, workplace-plausible vocabulary used to name folders and to seed titles/tags.
/// These are generic organisational terms (not copied from any real vault) in English and
/// Russian so a seeded tree reads like a normal company knowledge base rather than lorem ipsum.
/// </summary>
internal static class TopicWords
{
    // Folder/category segment candidates. Pure alphanumeric or hyphenated; never contain '/'.
    public static readonly string[] En =
    [
        "Engineering", "Operations", "Infrastructure", "Runbooks", "Incidents", "Postmortems",
        "Meetings", "Standups", "Retros", "Reviews", "Planning", "Strategy", "Roadmap", "OKRs",
        "HR", "Recruiting", "Onboarding", "Offboarding", "Payroll", "Benefits", "Performance",
        "Mentorship", "Training", "Policies", "Handbook", "Legal", "Compliance", "Contracts",
        "Finance", "Budget", "Invoicing", "Expenses", "Procurement", "Vendors", "Marketing",
        "Campaigns", "Brand", "Content", "SEO", "Sales", "Pipeline", "CRM", "Deals", "Renewals",
        "Product", "Research", "Discovery", "Specs", "RFCs", "ADRs", "Design", "UX", "Accessibility",
        "Architecture", "Security", "Audits", "PenTests", "IncidentResponse", "Support", "Tickets",
        "SLAs", "Knowledge", "Wiki", "Docs", "Changelog", "Releases", "Deployments", "Rollbacks",
        "Monitoring", "Alerts", "Dashboards", "SLOs", "Capacity", "Backups", "Migrations",
        "Testing", "QA", "Automation", "Performance", "Reliability", "SRE", "DevOps", "Frontend",
        "Backend", "Mobile", "Data", "Analytics", "ML", "Platform", "Tooling", "Workshops",
        "Events", "Announcements", "Holidays", "Travel", "Facilities", "Surveys", "Feedback",
        "Goals", "Growth", "Retention", "Partnerships", "Localization"
    ];

    public static readonly string[] Ru =
    [
        "Инженерия", "Операции", "Инфраструктура", "Регламенты", "Инциденты", "Разборы",
        "Совещания", "Планёрки", "Ретроспективы", "Ревью", "Планирование", "Стратегия",
        "Дорожная-карта", "Цели", "Кадры", "Подбор", "Адаптация", "Увольнения", "Зарплата",
        "Льготы", "Производительность", "Наставничество", "Обучение", "Политики", "Справочник",
        "Юристы", "Комплаенс", "Договоры", "Финансы", "Бюджет", "Оплата", "Расходы", "Закупки",
        "Поставщики", "Маркетинг", "Кампании", "Бренд", "Контент", "Продажи", "Воронка", "Сделки",
        "Продукт", "Исследования", "Спецификации", "Дизайн", "Юзабилити", "Доступность",
        "Архитектура", "Безопасность", "Аудиты", "Инцидент-менеджмент", "Поддержка", "Заявки",
        "База-знаний", "Документация", "Объявления", "Релизы", "Деплои", "Откаты", "Мониторинг",
        "Алерты", "Дашборды", "Ёмкость", "Бэкапы", "Миграции", "Тестирование", "Автоматизация",
        "Надёжность", "Платформа", "Инструменты", "Воркшопы", "Мероприятия", "Праздники",
        "Командировки", "Помещения", "Опросы", "Отзывы", "Рост", "Удержание", "Партнёрства",
        "Локализация", "Дежурства"
    ];

    /// <summary>
    /// A fixed pool of ~200 tag names (mixed en/ru, generic organisational labels) so a
    /// tag-frequency benchmark sees realistic hot/cold tags. Hyphens and both scripts are allowed.
    /// </summary>
    public static readonly string[] Tags =
    [
        "urgent", "draft", "reviewed", "approved", "archived", "meeting-notes", "decision",
        "action-item", "question", "follow-up", "blocked", "done", "wip", "backlog", "triaged",
        "budget", "forecast", "q1", "q2", "q3", "q4", "2023", "2024", "2025", "confidential",
        "internal", "external", "public", "reference", "how-to", "tutorial", "checklist",
        "template", "policy", "process", "guideline", "spec", "rfc", "adr", "faq", "glossary",
        "bug", "incident", "postmortem", "retro", "root-cause", "mitigation", "hotfix",
        "release-notes", "changelog", "deploy", "rollback", "migration", "deprecation",
        "research", "spike", "prototype", "poc", "experiment", "findings", "design-doc",
        "architecture", "api", "database", "schema", "migration-script", "frontend", "backend",
        "mobile", "infra", "cloud", "kubernetes", "terraform", "security", "compliance", "audit",
        "vulnerability", "patch", "legal", "contract", "finance", "invoice", "expense", "payroll",
        "hr", "hiring", "interview", "offer", "onboarding", "offboarding", "benefits", "ops",
        "sales", "pipeline", "deal", "renewal", "churn", "marketing", "campaign", "brand",
        "product", "roadmap", "discovery", "support", "ticket", "sla", "slo", "okr", "kpi",
        "metric", "report", "dashboard", "analytics", "survey", "feedback", "mentorship",
        "training", "workshop", "webinar", "conference", "team", "cross-team", "stakeholder",
        "escalation", "outage", "performance", "scalability", "reliability", "monitoring",
        "alerting", "observability", "backup", "disaster-recovery", "capacity", "cost",
        "vendor", "procurement", "sustainability", "accessibility", "localization", "i18n",
        "ai", "ml", "data", "etl", "warehouse", "срочно", "черновик", "проверено", "архив",
        "решение", "вопрос", "бюджет", "конфиденциально", "справка", "инструкция", "шаблон",
        "политика", "процесс", "регламент", "спецификация", "инцидент", "разбор", "онбординг",
        "обучение", "объявление", "релиз", "миграция", "исследование", "дизайн", "архитектура",
        "безопасность", "аудит", "финансы", "кадры", "отчёт", "аналитика", "опрос", "отзыв",
        "команда", "эскалация", "проблема", "метрика", "цель", "рост", "ретроспектива"
    ];
}
