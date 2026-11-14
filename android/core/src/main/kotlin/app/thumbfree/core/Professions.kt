package app.thumbfree.core

/**
 * "What do you do?" from the first-run guide. Used only on this phone: picking a role adds that field's names and
 * jargon to "Words I use often", so they're spelled right from the first dictation. Entries are proper nouns, brands
 * and acronyms. Never ordinary words that are also names (Notion, Excel, Medium): listed words are written exactly as listed,
 * so "excel at" would become "Excel at". And no ordinary words of 8+ letters, which the near-miss correction could
 * reach from other words ("escalating" → "escalation").
 */
object Professions {
    val all: Map<String, List<String>> = linkedMapOf(
        "Developer" to listOf("GitHub", "GitHub Copilot", "pull request", "Kubernetes", "Docker", "API", "JSON", "TypeScript", "JavaScript", "Python", "PostgreSQL", "VS Code"),
        "Product" to listOf("roadmap", "OKR", "KPI", "PRD", "Jira", "Figma", "go-to-market"),
        "Founder / CEO" to listOf("OKR", "KPI", "ARR", "go-to-market", "term sheet", "LinkedIn"),
        "Sales" to listOf("CRM", "Salesforce", "HubSpot", "LinkedIn"),
        "Marketing" to listOf("SEO", "LinkedIn", "Instagram", "YouTube", "HubSpot", "Google Analytics", "CTA"),
        "Customer support" to listOf("Zendesk", "Freshdesk", "SLA"),
        "Healthcare" to listOf("MRI", "CT scan", "ECG", "ibuprofen", "paracetamol", "amoxicillin", "BP", "OPD"),
        "Educator" to listOf("Google Classroom", "syllabus", "rubric", "PTM", "CBSE"),
        "Student" to listOf("Google Classroom", "syllabus", "GPA", "CGPA"),
        "Writer / creator" to listOf("Substack", "YouTube", "Instagram"),
        "Recruiting" to listOf("LinkedIn", "Naukri", "CTC"),
        "Legal" to listOf("NDA", "affidavit", "indemnity", "plaintiff", "defendant"),
        "Data analysis" to listOf("SQL", "Power BI", "Tableau", "pandas", "Jupyter"),
        "Operations" to listOf("SOP", "KPI", "SAP Ariba"),
        "Real estate" to listOf("RERA", "EMI", "escrow"),
        "Other" to emptyList(),
    )

    /** [current] word list with [role]'s words added (case-insensitive, nothing removed, order kept). */
    fun addTo(current: String, role: String): String {
        val existing = Vocabulary.parse(current).map { it.lowercase() }.toMutableSet()
        val added = all[role].orEmpty().filter { existing.add(it.lowercase()) }
        if (added.isEmpty()) return current
        val base = current.trimEnd()
        return (if (base.isEmpty()) "" else "$base\n") + added.joinToString("\n")
    }
}
