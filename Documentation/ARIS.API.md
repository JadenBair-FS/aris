# ARIS.API Documentation

## Overview
`ARIS.API` is the core backend service for the ARIS platform. It provides RESTful endpoints for searching the Dictionary (Roles and Skills), ingesting resumes into the symmetric vector space, and performing RAG-based recommendations.

## Tech Stack
*   **Framework:** ASP.NET Core (.NET 10)
*   **Database:** PostgreSQL 17 (via `ARIS.Shared`) with `pgvector`
*   **AI/ML:** 
    *   `Microsoft.Extensions.AI` for abstraction
    *   `OllamaSharp` for Ollama API communication
    *   `Ollama` (local) running `all-minilm` for embeddings (384d)
    *   `Ollama` (local) running `llama3.1` for chat completions
*   **PDF Processing:** `PdfPig`

## Endpoints

### Dictionary

#### 1. Search Roles
*   **URL:** `POST /api/dictionary/search/roles`
*   **Description:** Performs a vector similarity search to find roles matching the query.
*   **Body:**
    ```json
    {
      "query": "software engineer"
    }
    ```
*   **Response:** List of `RefRole` objects.

#### 2. Search Skills
*   **URL:** `POST /api/dictionary/search/skills`
*   **Description:** Performs a vector similarity search to find skills matching the query.
*   **Body:**
    ```json
    {
      "query": "react"
    }
    ```
*   **Response:** List of `RefSkill` objects.

#### 3. Recommend Jobs (RAG)
*   **URL:** `POST /api/dictionary/recommend/jobs`
*   **Description:** Uses RAG to analyze a user's prompt (e.g., "I know C# and React") and recommend suitable job roles from the database.
*   **Process:**
    1.  **Embed:** Converts user query to vector.
    2.  **Retrieve:** Finds top 5 matching `RefRole` entries.
    3.  **Generate:** Sends the user query + retrieved role details to Llama 3.1 to generate a personalized response.
*   **Body:**
    ```json
    {
      "query": "I am good at C# and React, what are some good jobs for me?"
    }
    ```
*   **Response:** String (Markdown formatted recommendation).

#### 4. Recommend Skills (RAG)
*   **URL:** `POST /api/dictionary/recommend/skills`
*   **Description:** Uses RAG to perform a skill gap analysis.
*   **Process:**
    1.  **Embed:** Converts user query to vector to identify the *target role*.
    2.  **Retrieve:** Finds the best matching `RefRole` and its required `RefSkill` list.
    3.  **Generate:** Sends the user query + required skills to Llama 3.1 to generate a gap analysis and study guide.
*   **Body:**
    ```json
    {
      "query": "I want to be a software developer, what are the skills I need to succeed?"
    }
    ```
*   **Response:** String (Markdown formatted analysis).

### Resume

#### 1. Upload Resume
*   **URL:** `POST /api/Resume/upload`
*   **Description:** Ingests a PDF resume, extracts structured data, and generates a professional identity vector.
*   **Parameters (Multipart/Form-Data):**
    *   `file`: The PDF resume file (Required).
    *   `userId`: The unique identifier for the user (Required).
*   **Internal Pipeline:**
    1.  **PDF Parsing:** Uses `PdfPig` to extract raw text.
    2.  **Clean Signal Extraction:** Uses `llama3.1` to map messy text to a standardized JSON schema (Roles, Skills, Education, Experience).
    3.  **Symmetric Vectorization:** Concatenates extracted roles and skills to generate a 384d embedding using `all-minilm`.
    4.  **Persistence:** Saves the `UserProfile` to PostgreSQL, including the raw text (as JSON), the Clean Signal, and the vector.
*   **Response:** 
    ```json
    {
      "message": "Resume processed and ingested successfully."
    }
    ```

## Configuration
Ensure `appsettings.json` or `appsettings.Development.json` points to the correct Database and Ollama instance.
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=aris_db;Username=aris_admin;Password=aris_password_local"
  }
}
```
The Ollama URL is currently hardcoded to `http://localhost:11434` in `Program.cs` for simplicity in this phase.