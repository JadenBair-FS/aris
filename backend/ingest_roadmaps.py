import os
import json
from neo4j import GraphDatabase
from dotenv import load_dotenv

load_dotenv()

URI = os.getenv("NEO4J_URI", "bolt://localhost:7687")
USER = os.getenv("NEO4J_USERNAME", "neo4j")
PASSWORD = os.getenv("NEO4J_PASSWORD", "password123")

ROADMAPS_DIR = os.path.join(os.path.dirname(__file__), "data", "roadmaps")

# Explicit exclusions based on user request
EXCLUDED_ROADMAPS = [
    "backend-performance-best-practices",
    "frontend-performance-best-practices",
    "code-review-best-practices",
    "code-review",
    "aws",
]

class RoadmapIngestor:
    def __init__(self):
        self.driver = GraphDatabase.driver(URI, auth=(USER, PASSWORD))

    def close(self):
        self.driver.close()

    def normalize_name(self, name):
        """Normalizes names to merge nodes correctly."""
        if not name: return ""
        n = name.strip()
        # Common cleanup
        if n.lower() == "nextjs": return "Next.js"
        if n.lower() == "reactjs": return "React"
        if n.lower() == "nodejs": return "Node.js"
        return n

    def ingest_roadmap(self, file_path):
        with open(file_path, 'r') as f:
            data = json.load(f)

        slug = data.get("slug")
        if slug in EXCLUDED_ROADMAPS:
            print(f"Skipping excluded roadmap: {slug}")
            return

        title = data.get("title", {}).get("page", slug)
        nodes = data.get("nodes", [])
        edges = data.get("edges", [])

        print(f"Ingesting roadmap: {title} ({len(nodes)} nodes)...")

        with self.driver.session() as session:
            # Create Domain (The high-level category)
            session.run("""
                MERGE (d:__Entity__ {name: $name})
                SET d:Domain, d.slug = $slug, d.entity_type = 'Domain'
                WITH d
                # Link to O*NET Job if title matches
                MATCH (j:Job) 
                WHERE toLower(j.name) CONTAINS toLower($name) 
                   OR toLower($name) CONTAINS toLower(j.name)
                MERGE (d)-[:REPRESENTS]->(j)
            """, name=title, slug=slug)

            # Map internal ID to Normalized Name for Edge Creation
            id_to_name = {}

            # Create Topic/Subtopic Nodes (Merged by Name)
            for node in nodes:
                node_type = node.get("type")
                raw_label = node.get("data", {}).get("label")
                node_id = node.get("id")

                if node_type in ["topic", "subtopic"] and raw_label:
                    name = self.normalize_name(raw_label)
                    id_to_name[node_id] = name

                    session.run("""
                        MERGE (s:__Entity__ {name: $name})
                        SET s:Skill, s.entity_type = 'Skill'
                        WITH s
                        MATCH (d:Domain {name: $dname})
                        MERGE (d)-[:HAS_SKILL]->(s)
                    """, name=name, dname=title)

            # Create Edges (Relationships)
            for edge in edges:
                source_name = id_to_name.get(edge.get("source"))
                target_name = id_to_name.get(edge.get("target"))

                if source_name and target_name and source_name != target_name:
                    # Direction: source REQUIRES target
                    session.run("""
                        MATCH (a:__Entity__ {name: $source})
                        MATCH (b:__Entity__ {name: $target})
                        MERGE (a)-[:REQUIRES]->(b)
                    """, source=source_name, target=target_name)

    def run_all(self):
        if not os.path.exists(ROADMAPS_DIR):
            print(f"Directory not found: {ROADMAPS_DIR}")
            return
        files = [f for f in os.listdir(ROADMAPS_DIR) if f.endswith(".json")]
        for filename in files:
            self.ingest_roadmap(os.path.join(ROADMAPS_DIR, filename))

if __name__ == "__main__":
    ingestor = RoadmapIngestor()
    try:
        ingestor.run_all()
    finally:
        ingestor.close()