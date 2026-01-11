import os
import csv
from neo4j import GraphDatabase
from dotenv import load_dotenv

load_dotenv()

URI = os.getenv("NEO4J_URI", "bolt://localhost:7687")
USER = os.getenv("NEO4J_USERNAME", "neo4j")
PASSWORD = os.getenv("NEO4J_PASSWORD", "password123")

ONET_DIR = os.path.join(os.path.dirname(__file__), "data", "onet", "db_30_1_text")

class ONetIngestor:
    def __init__(self):
        self.driver = GraphDatabase.driver(URI, auth=(USER, PASSWORD))

    def close(self):
        self.driver.close()

    def normalize_name(self, name):
        if not name: return ""
        n = name.strip()
        if n.lower() == "nextjs": return "Next.js"
        if n.lower() == "reactjs": return "React"
        if n.lower() == "nodejs": return "Node.js"
        return n

    def ingest_occupations(self):
        file_path = os.path.join(ONET_DIR, "Occupation Data.txt")
        print("Ingesting O*NET Occupations...")
        with self.driver.session() as session:
            with open(file_path, 'r', encoding='utf-8') as f:
                reader = csv.DictReader(f, delimiter='\t')
                for row in reader:
                    session.run("""
                        MERGE (j:__Entity__ {name: $title})
                        SET j:Job, j.id = $code, j.description = $desc, j.entity_type = 'Job'
                    """, code=row["O*NET-SOC Code"], title=row["Title"], desc=row["Description"])

    def _ingest_element_file(self, filename, label, rel_type):
        file_path = os.path.join(ONET_DIR, filename)
        if not os.path.exists(file_path):
            print(f"File not found: {filename}")
            return
        
        print(f"Ingesting {filename}...")
        with self.driver.session() as session:
            with open(file_path, 'r', encoding='utf-8') as f:
                reader = csv.DictReader(f, delimiter='\t')
                for row in reader:
                    # Scale ID 'IM' is Importance. Usually 1-5.
                    if row["Scale ID"] == "IM" and float(row["Data Value"]) >= 3.0:
                        session.run(f"""
                            MATCH (j:Job {{id: $code}})
                            MERGE (e:__Entity__ {{name: $name}})
                            SET e:{label}, e.entity_type = '{label}'
                            MERGE (j)-[:{rel_type} {{importance: $val}}]->(e)
                        """, code=row["O*NET-SOC Code"], name=row["Element Name"], val=float(row["Data Value"]))

    def ingest_skills(self):
        self._ingest_element_file("Skills.txt", "Skill", "HAS_SKILL")

    def ingest_knowledge(self):
        self._ingest_element_file("Knowledge.txt", "Knowledge", "HAS_KNOWLEDGE")

    def ingest_activities(self):
        self._ingest_element_file("Work Activities.txt", "Activity", "HAS_ACTIVITY")

    def ingest_abilities(self):
        self._ingest_element_file("Abilities.txt", "Ability", "HAS_ABILITY")

    def ingest_tech_skills(self):
        file_path = os.path.join(ONET_DIR, "Technology Skills.txt")
        print("Ingesting O*NET Technology Skills...")
        with self.driver.session() as session:
            with open(file_path, 'r', encoding='utf-8') as f:
                reader = csv.DictReader(f, delimiter='\t')
                for row in reader:
                    name = self.normalize_name(row["Example"])
                    session.run("""
                        MATCH (j:Job {id: $code})
                        MERGE (t:__Entity__ {name: $name})
                        SET t:Tool, t.entity_type = 'Tool'
                        MERGE (j)-[:REQUIRES_TOOL]->(t)
                    """, code=row["O*NET-SOC Code"], name=name)

    def ingest_alternate_titles(self):
        file_path = os.path.join(ONET_DIR, "Alternate Titles.txt")
        print("Ingesting O*NET Alternate Titles...")
        with self.driver.session() as session:
            with open(file_path, 'r', encoding='utf-8') as f:
                reader = csv.DictReader(f, delimiter='\t')
                for row in reader:
                    session.run("""
                        MATCH (j:Job {id: $code})
                        SET j.alternate_titles = 
                            CASE WHEN j.alternate_titles IS NULL THEN [$alt]
                            ELSE j.alternate_titles + $alt END
                    """, code=row["O*NET-SOC Code"], alt=row["Alternate Title"])

    def run_all(self):
        if not os.path.exists(ONET_DIR): 
            print(f"ONET directory not found: {ONET_DIR}")
            return
        self.ingest_occupations()
        self.ingest_alternate_titles()
        self.ingest_skills()
        self.ingest_knowledge()
        self.ingest_activities()
        self.ingest_abilities()
        self.ingest_tech_skills()

if __name__ == "__main__":
    ingestor = ONetIngestor()
    try:
        ingestor.run_all()
    finally:
        ingestor.close()