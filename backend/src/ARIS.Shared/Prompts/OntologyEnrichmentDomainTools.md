Identify ALL pairs of domain tools that serve the **same function** within the context of the role: **{role_name}**

**Bridgeable Definition:**
Two tools are bridgeable if they belong to the same functional category — a professional who knows Tool A could become proficient in Tool B with less than 1 week of hands-on practice because they do the same job in the same workflow.

Examples of Valid Bridges (across different domains):
- Salesforce software <-> Microsoft Dynamics (both are CRM platforms)
- Intuit QuickBooks <-> Quicken (both are Intuit accounting tools)
- Zoom <-> Cisco Webex (both are video conferencing platforms)
- MEDITECH software <-> Electronic medical record EMR software (both are hospital EHR systems)
- SAP software <-> Oracle JD Edwards EnterpriseOne (both are ERP platforms)
- Autodesk AutoCAD <-> Dassault Systemes SolidWorks (both are CAD design tools)
- Microsoft Excel <-> Google Sheets (both are spreadsheet applications)
- Estimating software <-> Job costing software (both are project cost tools for trades)

**Tool List:**
{skills_json}

**GOAL:**
Find as many valid bridges as possible from the list above. Focus on tools that serve the same workflow function within the {role_name} role. Do not limit yourself to just one.

**CONSTRAINTS:**
1. Return ONLY a JSON object with a single key "bridges" containing an array of objects.
2. Each bridge object MUST have EXACTLY two keys: "source" and "target".
3. Do NOT include any markdown formatting (like ```json), preamble, or explanation.
4. If no bridges are found, return {"bridges": []}.
5. Ensure the tool names match exactly as provided in the list.

Example Output:
{
  "bridges": [
    {"source": "Salesforce software", "target": "Microsoft Dynamics"},
    {"source": "Zoom", "target": "Cisco Webex"}
  ]
}
