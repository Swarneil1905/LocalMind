"""Conservative detection of explicit task-writing intent.

This returns manual fallback text for older clients. Review-capable desktop
clients use the same intent boundary to prepare typed proposals instead.
Ambiguous requests stay with chat; this module never writes a task.
"""

import re


_COMMAND = re.compile(
    r"^(?:please\s+)?"
    r"(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?|"
    r"(?:i'd like|i would like|i want)\s+you\s+to\s+)?"
    r"(?:please\s+)?(?P<verb>save|create|add)\b(?P<body>.*)$",
    re.IGNORECASE,
)
_QUOTED = re.compile(
    r'```.*?```|`[^`]*`|"[^"\n]*"|“[^”\n]*”|'
    r"(?<!\w)'[^'\n]*'(?!\w)|‘[^’\n]*’",
    re.DOTALL,
)
_TASK_OBJECT = re.compile(
    r"^(?:(?:me|us)\s+)?(?:(?:a|an|one|the|new|another)\s+)?"
    r"(?:new\s+)?task\b(?!\.\w)(?P<tail>.*)$"
)
_TASK_CONTENT = re.compile(
    r"^\s*(?:description|draft|example|template|outline|wording|instructions|"
    r"definition|creation|handler|function|class|component|api|schema)\b"
)
_DRAFT_ONLY = re.compile(
    r"\b(?:as\s+(?:an?\s+)?(?:example|draft|template|exercise)|"
    r"(?:don't|do not|never)\s+(?:actually\s+)?(?:save|add|create|store|persist))\b"
)
_CODE_REQUEST = re.compile(
    r"\b(?:in|using|with)\s+(?:python|javascript|typescript|java|rust|asyncio|celery)\b|"
    r"\b(?:asyncio|celery|event loop|pseudocode|code snippet)\b"
)
_PERSISTENCE = re.compile(
    r"\b(?:called|named|titled)\s+\S+|"
    r"\b(?:in|to|under|inside)\s+(?:\S+\s+){0,8}project\b|"
    r"\b(?:to|in|on)\s+(?:(?:my|the|our)\s+)?(?:tasks|task list|task board)\b"
)
_LEARNING_TASK = re.compile(
    r"^\s*(?:for|to)\s+(?:(?:me|us)\s+(?:to\s+)?)?"
    r"(?:learn|learning|practice|practicing|practising|teach|teaching|study|studying)\b"
)
_AS_TASK = re.compile(r"\bas\s+(?:(?:a|one|new)\s+)?task\b(?!\.\w)(?P<tail>.*)$")
_TO_TASK_LIST = re.compile(
    r"\bto\s+(?:(?:my|the|our)\s+)?(?:tasks|task list|task board)\b"
)

_RESPONSE = (
    "I haven’t created or saved a task. To add it, open **Tasks**, choose the intended "
    "project, enter the task title, and select **Add task**. If the project does not "
    "exist, create it in **Projects** first."
)


def unsupported_task_response(message: str) -> str | None:
    """Return honest manual steps only for a direct, unsupported task write.

    Quoted text cannot activate this boundary by itself. Quoted task titles are
    masked before exclusions so a title like 'Draft a Python example' remains a
    valid task title. Nothing is persisted, and no title or project is inferred.
    """
    command = _COMMAND.match(message.strip())
    if command is None:
        return None
    verb = command.group("verb").lower()
    body = " ".join(_QUOTED.sub("<quoted>", command.group("body")).lower().split())
    if _DRAFT_ONLY.search(body) or _CODE_REQUEST.search(body):
        return None

    task = _TASK_OBJECT.match(body)
    if task:
        tail = task.group("tail")
        if _TASK_CONTENT.match(tail):
            return None
        explicit_destination = _PERSISTENCE.search(tail)
        if _LEARNING_TASK.match(tail) and not explicit_destination:
            return None
        # "Create a task for learning" can mean invent an exercise. A named
        # task or a project/list destination makes the persistence intent clear.
        if verb in {"save", "add"} or explicit_destination:
            return _RESPONSE

    if verb in {"save", "add"}:
        as_task = _AS_TASK.search(body)
        if as_task and not _TASK_CONTENT.match(as_task.group("tail")):
            return _RESPONSE
        if _TO_TASK_LIST.search(body):
            return _RESPONSE
    return None
