"""Select local reference data for one conversation, never across project scopes."""
import json
import re

from app.db import conversations, knowledge, memories, projects
from app.search.planning import is_social_turn

_STOP = set("the and for that this with from have your you about please what when where which "
            "would could should can how does more some just into been their there them then "
            "help give tell make show want need explain explanation explanations example examples "
            "diagram flowchart details detail understand concept concepts learn learning teaching "
            "step steps deeply again further simpler briefly expand elaborate".split())

_FOLLOW_UP = re.compile(
    r"\b(?:it|its|that|this|these|those|them|they|their|above|previous|same|"
    r"more details|more detail|go deeper|tell me more|continue|elaborate)\b|"
    r"^(?:please\s+)?(?:give|show|draw|make|create)\s+(?:me\s+)?(?:an?\s+)?"
    r"(?:example|diagram|flowchart|analogy)\b", re.I,
)
_TOPIC_CHANGE = re.compile(
    r"^(?:please\s+)?(?:new (?:topic|question)|different (?:topic|question)|"
    r"switch (?:topics?|to)|moving on|instead[, :]|forget (?:that|this))\b", re.I,
)
_EXPLICIT_SUBJECT = re.compile(
    r"^(?:(?:please|now)\s*,?\s*)*(?:(?:can|could|would)\s+you\s+)?"
    r"(?:explain|describe|teach me|tell me about|what (?:is|are)|how (?:does|do))\s+"
    r"(?!(?:it|its|that|this|these|those|they|their|the above|the same|your|in more|"
    r"more|further|again)\b)\w", re.I,
)
MEMORY_BUDGET_CHARS = 1500  # about 400 tokens of saved memories per request
_STYLE_PREFERENCE = re.compile(
    r"^(?:you|the user|user)\s+prefers?\s+(?:(?:learning|explanations?)\s+"
    r"(?:through|with|using)\s+)?(?:(?:small|short|worked|concrete|simple|visual)\s+)*"
    r"(?:examples?|equations|diagrams|step[- ]by[- ]step explanations?)"
    r"(?:\s+before\s+(?:equations|code|theory))?[.!]?\s*$", re.I,
)


def resolve_history(history: list[dict], conversation_id: str | None = None,
                    project_id: str | None = None) -> list[dict]:
    """Saved chats use their own history, never a client-supplied cross-chat list.

    Unsaved drafts can carry ordinary turns, but role injection and explicitly
    owned rows from another conversation are discarded. Clients must finish
    saving a completed turn before allowing its follow-up to be sent.
    """
    resolve_project(project_id, conversation_id)
    rows = conversations.get_messages(conversation_id) if conversation_id else history
    return [{"role": row["role"], "content": row["content"],
             **({"message_id": row["id"]} if conversation_id and isinstance(row.get("id"), str) else {})}
            for row in rows
            if isinstance(row, dict) and row.get("role") in ("user", "assistant")
            and isinstance(row.get("content"), str)
            and row.get("status", "completed") == "completed" and not row.get("partial", False)
            and (conversation_id is not None or not row.get("conversation_id"))]


def retrieval_query(message: str, history: list[dict]) -> str:
    """Keep the latest explicit subject and its referential follow-ups together.

    This bounded lexical heuristic does not infer a topic from saved memories.
    An explicit new request searches on its own; it cannot revive the prior
    subject just because that subject was discussed one turn ago.
    """
    current = message.strip()
    if (is_social_turn(current) or _TOPIC_CHANGE.search(current)
            or _EXPLICIT_SUBJECT.search(current) or not _FOLLOW_UP.search(current)):
        return current[-4000:]
    chain = [current]
    for row in reversed(history[-24:]):
        if row.get("role") != "user" or not isinstance(row.get("content"), str):
            continue
        content = row["content"].strip()
        if is_social_turn(content):
            continue
        chain.insert(0, content)
        if (_TOPIC_CHANGE.search(content) or _EXPLICIT_SUBJECT.search(content)
                or not _FOLLOW_UP.search(content)):
            break
    return "\n".join(chain)[-4000:]


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
        if learning and _STYLE_PREFERENCE.fullmatch(content):
            score += 2
        if score:
            ranked.append((score, -index, item))
    # Best matches first, within a size budget so memory never crowds out the question and sources.
    chosen, used = [], 0
    for _, _, item in sorted(ranked, key=lambda row: row[:2], reverse=True)[:limit]:
        if chosen and used + len(item["content"]) > MEMORY_BUDGET_CHARS:
            break
        chosen.append(item)
        used += len(item["content"])
    return chosen


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
    query = retrieval_query(message, history)
    references = []
    if memory_enabled:
        selected = select_memories(query, memories.list_all(), project_id=project_id,
                                   conversation_id=conversation_id)
        if selected:
            query_terms = _terms(query)
            # The app shows which memories shaped the answer and which words matched.
            used = [{"id": item["id"], "content": item["content"],
                     "matched": sorted(query_terms & _terms(item["content"]))[:4]} for item in selected]
            references.append({"label": "Relevant saved memories",
                               "content": "\n".join(item["content"] for item in selected),
                               "source": {"kind": "memory", "title": "Saved memories", "url": "",
                                          "snippet": "", "memories": used}})
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
        for number, chunk in enumerate(chunks, 1):
            citation = f"K{number}"
            source = {"kind": "knowledge", "citation": citation, "chunk_id": chunk["id"],
                      "source_id": chunk["source_id"], "project_id": project_id,
                      "title": chunk["file_path"].replace("\\", "/").rsplit("/", 1)[-1],
                      "url": "", "snippet": chunk["content"], "file_path": chunk["file_path"],
                      "location": chunk.get("location", f"Passage {chunk['chunk_index'] + 1}")}
            references.append({"label": f"[{citation}] {source['title']} · {source['location']}",
                               "content": chunk["content"], "source": source})
        # A conversation may move while retrieval waits for the local model.
        # Preserve the resolved scope for this turn or fail before inference.
        if resolve_project(project_id, conversation_id) != project_id:
            raise ValueError("This chat's project changed. Reopen the chat before sending.")
    return references
