"""
HyDE - Hypothetical Document Embeddings (Phase 5.5)

Instead of embedding the raw user query for vector search, HyDE uses the
Speed model to generate a short hypothetical ideal answer first, then embeds
that answer. Answer embeddings match the embedding space of indexed document
chunks better than query embeddings do, improving retrieval recall without
any retraining or labeling.

Reference: "Precise Zero-Shot Dense Retrieval without Relevance Labels"
           Gao et al., 2022 - https://arxiv.org/abs/2212.10496

Typical usage (called from db/knowledge.py search):

    from app.rag.hyde import hyde_embed

    vector = await hyde_embed(
        query="What are the project deadlines?",
        speed_model="qwen2.5:1.5b",
        embed_model="nomic-embed-text",
    )
    # Pass vector directly to LanceDB ANN search
"""

import asyncio
import logging

import httpx

from app.db.knowledge import embed_text

logger = logging.getLogger(__name__)

OLLAMA_BASE = "http://127.0.0.1:11434"

# Prompt instructs the model to write a dense factual answer, not hedge.
# Low temperature keeps output consistent across calls.
_HYDE_PROMPT = """\
A user is searching a personal knowledge base and asked:

"{query}"

Write a concise, factual 2-4 sentence answer as if the answer exists in the \
knowledge base. Do not hedge or say you are guessing. Write only the answer, \
no preamble, no meta-commentary."""


async def _generate_hypothetical_answer(
    query: str,
    speed_model: str,
    timeout_sec: float,
) -> str:
    """
    Call Ollama (non-streaming) with the Speed model to produce a short
    hypothetical ideal answer. Raises on timeout or HTTP error.
    """
    prompt = _HYDE_PROMPT.format(query=query)
    payload = {
        "model": speed_model,
        "prompt": prompt,
        "stream": False,
        "options": {
            "num_predict": 120,   # keep it short - we only need the embedding
            "temperature": 0.1,   # low variance so embedding is stable
        },
    }
    timeout = httpx.Timeout(connect=2.0, read=timeout_sec, write=2.0, pool=2.0)
    async with httpx.AsyncClient(timeout=timeout) as client:
        resp = await client.post(f"{OLLAMA_BASE}/api/generate", json=payload)
        resp.raise_for_status()
        return resp.json().get("response", "").strip()


async def hyde_embed(
    query: str,
    speed_model: str,
    embed_model: str = "nomic-embed-text",
    timeout_sec: float = 2.0,
) -> list[float]:
    """
    Return an embedding vector for retrieval using HyDE.

    Steps:
      1. Call Speed model to generate a hypothetical ideal answer (<=2s).
      2. Embed that hypothetical answer with embed_model.
      3. On any failure (timeout, model error, empty response), fall back
         to embedding the raw query - no exception is raised to the caller.

    Args:
        query:       The user search query.
        speed_model: Ollama model name for the Speed tier (generation step).
        embed_model: Ollama model name for embeddings.
        timeout_sec: Wall-clock budget for the generation step. On expiry,
                     falls back to embedding the raw query.

    Returns:
        list[float] embedding vector ready for LanceDB ANN search.
    """
    hypothetical: str | None = None
    try:
        hypothetical = await asyncio.wait_for(
            _generate_hypothetical_answer(query, speed_model, timeout_sec),
            timeout=timeout_sec,
        )
    except asyncio.TimeoutError:
        logger.debug("HyDE: generation timed out for %r - using raw query", query[:60])
    except httpx.TimeoutException:
        logger.debug("HyDE: HTTP timeout for %r - using raw query", query[:60])
    except Exception as exc:
        logger.debug("HyDE: generation failed (%s) - using raw query", exc)

    if hypothetical:
        logger.debug(
            "HyDE: generated %d chars for %r",
            len(hypothetical),
            query[:60],
        )
        text_to_embed = hypothetical
    else:
        text_to_embed = query

    return await embed_text(text_to_embed, model=embed_model)
