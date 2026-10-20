"""Select local reference data for one conversation, never across project scopes."""
import json
import re

from app.db import conversations, knowledge, memories, projects

_STOP = set("the and for that this with from have your you about please what when where which "
            "would could should can how does more some just into been their there them then "
            "help give tell make show want need".split())


def resolve_project(project_id: str | None, conversation_id: str | None) -> str | None:
    if conversation_id:
        conversation = conversations.get_conversation(conversation_id)
        if not conversation:
            raise ValueError("This conversation no longer exists. Start a new chat.")
        stored = conversation.get("project_id")
        if project_id is not None and stored != project_id:
            raise ValueError("This chat's project changed. Reopen the chat before sending.")
        project_id = stored
    if project_id is not None and not projects.get_project(project_id):
        raise ValueError("This project no longer exists. Move the chat to an available project.")
    return project_id


def _terms(text: str) -> set[str]:
    return set(re.findall(r"\w{3,}", text.lower())) - _STOP


def select_memories(query: str, candidates: list[dict], *,
                    project_id: str | None, conversation_id: str | None,
                    limit: int = 6) -> list[dict]:
    """Bounded lexical relevance, not a claim of semantic understanding.

    Project memories are usable only in their project. Unassigned conversation
    memories stay in that conversation. Legacy global facts require relevance.
    """
    terms = _terms(query)
    learning = bool(re.search(r"\b(explain|understand|teach|example|learn|walk me)\b", query, re.I))
    ranked = []
    for index, item in enumerate(candidates):
        if item.get("project_id") is not None:
            if item["project_id"] != project_id:
                continue
        elif item.get("conversation_id") is not None and item["conversation_id"] != conversation_id:
            continue
        content = item.get("content", "")
        words = _terms(content)
        score = len(terms & words)
        if learning and re.search(r"\bprefer\w*\b", content, re.I) and words & {
            "examples", "example", "learn", "learning", "equations", "explanations",
        }:
            score += 2
        if score:
            ranked.append((score, -index, item))
    return [item for _, _, item in sorted(ranked, key=lambda row: row[:2], reverse=True)[:limit]]


async def collect_context(
    message: str,
    history: list[dict],
    project_id: str | None = None,
    conversation_id: str | None = None,
    memory_enabled: bool = False,
    knowledge_enabled: bool = False,
    embed_model: str = "nomic-embed-text",
    hyde_enabled: bool = False,
) -> list[dict[str, str]]:
    project_id = resolve_project(project_id, conversation_id)
    recent = [m["content"] for m in history[-6:]
              if m.get("role") == "user" and isinstance(m.get("content"), str)]
    query = "\n".join([*recent[-2:], message])[-4000:]
    references = []
    if memory_enabled:
        selected = select_memories(query, memories.list_all(), project_id=project_id,
                                   conversation_id=conversation_id)
        if selected:
            references.append({"label": "Relevant saved memories",
                               "content": "\n".join(item["content"] for item in selected)})
    if project_id:
        project = projects.get_project(project_id)
        if not project:
            raise ValueError("This project no longer exists. Move the chat to an available project.")
        tasks = projects.list_tasks(project_id)
        references.append({
            "label": "Active project",
            "content": json.dumps({"name": project["name"], "summary": project.get("summary"),
                                   "tasks": [{"title": t["title"], "status": t["status"], "due_at": t["due_at"]}
                                             for t in tasks[:30]]}, ensure_ascii=False),
        })
    if knowledge_enabled:
        chunks = await knowledge.search(query, limit=5, embed_model=embed_model,
                                        hyde_enabled=hyde_enabled, project_id=project_id)
        references.extend({"label": f"Project source: {chunk['file_path']}" if project_id
                           else f"Unassigned source: {chunk['file_path']}",
                           "content": chunk["content"]} for chunk in chunks)
        # A conversation may move while retrieval waits for the local model.
        # Preserve the resolved scope for this turn or fail before inference.
        if resolve_project(project_id, conversation_id) != project_id:
            raise ValueError("This chat's project changed. Reopen the chat before sending.")
    return references
