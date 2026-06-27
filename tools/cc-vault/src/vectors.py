"""
Vault Vectors - SQLite-native vector storage for semantic search

Provides vector storage and similarity search for the Vault 2.0 platform.
Uses OpenAI embeddings and SQLite with struct-packed BLOBs for persistent storage.
Cosine similarity computed in pure Python (no numpy needed).
"""

import logging
import math
import struct
from pathlib import Path
from typing import List, Dict, Any, Optional
from datetime import datetime

try:
    import openai
    OPENAI_AVAILABLE = True
except ImportError:
    OPENAI_AVAILABLE = False

try:
    from .config import (
        VECTORS_PATH, EMBEDDING_MODEL, EMBEDDING_DIMENSIONS,
        OPENAI_API_KEY, VECTOR_COLLECTIONS,
        CHUNK_MAX_TOKENS, CHUNK_OVERLAP_TOKENS, CHUNK_THRESHOLD_TOKENS,
        HYBRID_VECTOR_WEIGHT, HYBRID_TEXT_WEIGHT
    )
    from .db import (
        init_db, vec_add, vec_add_batch, vec_get_all,
        vec_delete_by_id, vec_delete_by_metadata, vec_count, vec_get_embedding
    )
except ImportError:
    from config import (
        VECTORS_PATH, EMBEDDING_MODEL, EMBEDDING_DIMENSIONS,
        OPENAI_API_KEY, VECTOR_COLLECTIONS,
        CHUNK_MAX_TOKENS, CHUNK_OVERLAP_TOKENS, CHUNK_THRESHOLD_TOKENS,
        HYBRID_VECTOR_WEIGHT, HYBRID_TEXT_WEIGHT
    )
    from db import (
        init_db, vec_add, vec_add_batch, vec_get_all,
        vec_delete_by_id, vec_delete_by_metadata, vec_count, vec_get_embedding
    )

logger = logging.getLogger(__name__)

# struct format for packing/unpacking embedding floats
_EMBED_FMT = f'{EMBEDDING_DIMENSIONS}f'
_EMBED_SIZE = struct.calcsize(_EMBED_FMT)


def _pack_embedding(embedding: List[float]) -> bytes:
    """Pack a list of floats into a compact binary BLOB."""
    return struct.pack(_EMBED_FMT, *embedding)


def _unpack_embedding(blob: bytes) -> tuple:
    """Unpack a binary BLOB back into a tuple of floats."""
    return struct.unpack(_EMBED_FMT, blob)


def _cosine_similarity(a, b) -> float:
    """Compute cosine similarity between two vectors."""
    dot = 0.0
    norm_a = 0.0
    norm_b = 0.0
    for x, y in zip(a, b):
        dot += x * y
        norm_a += x * x
        norm_b += y * y
    denom = math.sqrt(norm_a) * math.sqrt(norm_b)
    if denom == 0:
        return 0.0
    return dot / denom


def _cosine_similarity_prenorm(a, b, a_norm: float) -> float:
    """Cosine similarity with pre-computed norm for vector a."""
    dot = 0.0
    norm_b = 0.0
    for x, y in zip(a, b):
        dot += x * y
        norm_b += y * y
    denom = a_norm * math.sqrt(norm_b)
    if denom == 0:
        return 0.0
    return dot / denom


def _vector_norm(v) -> float:
    """Compute L2 norm of a vector."""
    return math.sqrt(sum(x * x for x in v))


def _build_path_context(metadata: Dict[str, Any]) -> str:
    """Build a path context prefix from document metadata.

    Extracts title, path, and source from metadata and returns a short
    text prefix like "Document: My Title | Path: /some/dir/file.pdf\n"
    that gets prepended to chunk content for search indexing.
    """
    parts = []
    title = metadata.get('doc_title') or metadata.get('title') or ''
    if title:
        parts.append(f"Document: {title}")

    path = metadata.get('doc_path') or metadata.get('source') or ''
    if path:
        parts.append(f"Path: {path}")

    if not parts:
        return ''
    return ' | '.join(parts) + '\n'


class VaultVectors:
    """SQLite-backed vector store for Vault semantic search."""

    def __init__(self):
        """Initialize the vector store."""
        init_db(silent=True)

        # Initialize OpenAI client if available
        self.openai_client = None
        if OPENAI_AVAILABLE and OPENAI_API_KEY:
            self.openai_client = openai.OpenAI(
                api_key=OPENAI_API_KEY,
                timeout=30.0
            )

    def embed_text(self, text: str) -> List[float]:
        """Generate embedding for text using OpenAI."""
        if not self.openai_client:
            raise RuntimeError("OpenAI client not available. Set OPENAI_API_KEY environment variable.")

        try:
            response = self.openai_client.embeddings.create(
                model=EMBEDDING_MODEL,
                input=text
            )
            return response.data[0].embedding
        except (openai.APIError, openai.APIConnectionError, openai.APITimeoutError) as e:
            raise RuntimeError(f"OpenAI embedding failed: {e}")

    def embed_texts(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings for multiple texts using OpenAI."""
        if not self.openai_client:
            raise RuntimeError("OpenAI client not available. Set OPENAI_API_KEY environment variable.")

        try:
            response = self.openai_client.embeddings.create(
                model=EMBEDDING_MODEL,
                input=texts
            )
            return [item.embedding for item in response.data]
        except (openai.APIError, openai.APIConnectionError, openai.APITimeoutError) as e:
            raise RuntimeError(f"OpenAI batch embedding failed: {e}")

    def _add_to_collection(
        self,
        collection: str,
        id: str,
        embedding: List[float],
        document: Optional[str] = None,
        metadata: Optional[Dict[str, Any]] = None
    ) -> str:
        """Add a single item to a collection."""
        blob = _pack_embedding(embedding)
        vec_add(id, collection, blob, document, metadata)
        return id

    def _query_collection(
        self,
        collection: str,
        query_embedding: List[float],
        n_results: int = 10,
        filter_metadata: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """Query a collection using cosine similarity."""
        # Skip empty collections without a DB roundtrip
        count = vec_count(collection)
        if count == 0:
            return []

        rows = vec_get_all(collection)
        if not rows:
            return []

        # Pre-compute query norm once (saves ~40% of cosine sim work)
        query_norm = _vector_norm(query_embedding)
        if query_norm == 0:
            return []

        # Compute similarities
        scored = []
        for row in rows:
            # Apply metadata filter if specified
            if filter_metadata:
                row_meta = row.get('metadata', {})
                match = True
                for k, v in filter_metadata.items():
                    if row_meta.get(k) != v:
                        match = False
                        break
                if not match:
                    continue

            row_embedding = _unpack_embedding(row['embedding'])
            similarity = _cosine_similarity_prenorm(query_embedding, row_embedding, query_norm)
            distance = 1.0 - similarity
            scored.append({
                'id': row['id'],
                'document': row.get('document'),
                'metadata': row.get('metadata', {}),
                'distance': distance
            })

        # Sort by distance (lower = more similar)
        scored.sort(key=lambda x: x['distance'])
        return scored[:n_results]

    # ===========================================
    # DOCUMENT OPERATIONS
    # ===========================================

    def add_document(
        self,
        doc_id: str,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> str:
        """Add a document to the vector store."""
        embedding = self.embed_text(content)

        meta = metadata or {}
        meta["indexed_at"] = datetime.now().isoformat()

        return self._add_to_collection("documents", doc_id, embedding, content, meta)

    def query_documents(
        self,
        query: str,
        n_results: int = 10,
        filter_metadata: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """Query documents by semantic similarity."""
        query_embedding = self.embed_text(query)
        return self._query_collection("documents", query_embedding, n_results, filter_metadata)

    def delete_document(self, doc_id: str) -> bool:
        """Delete a document from the vector store."""
        vec_delete_by_id(doc_id, "documents")
        return True

    # ===========================================
    # CHUNK OPERATIONS
    # ===========================================

    def add_chunk(
        self,
        chunk_id: str,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> str:
        """Add a document chunk to the vector store."""
        embedding = self.embed_text(content)

        meta = metadata or {}
        meta["indexed_at"] = datetime.now().isoformat()

        return self._add_to_collection("chunks", chunk_id, embedding, content, meta)

    def add_chunks_batch(
        self,
        chunks: List[Dict[str, Any]]
    ) -> List[str]:
        """
        Add multiple chunks in a batch.

        Each chunk dict should have: id, content, metadata
        """
        if not chunks:
            return []

        ids = [c['id'] for c in chunks]
        contents = [c['content'] for c in chunks]
        metadatas = []

        for c in chunks:
            meta = c.get('metadata', {})
            meta["indexed_at"] = datetime.now().isoformat()
            metadatas.append(meta)

        # Batch embed all content
        embeddings = self.embed_texts(contents)

        # Build rows for batch insert
        rows = []
        for i, chunk_id in enumerate(ids):
            rows.append({
                'id': chunk_id,
                'embedding': _pack_embedding(embeddings[i]),
                'document': contents[i],
                'metadata': metadatas[i]
            })

        vec_add_batch(rows, "chunks")
        return ids

    def query_chunks(
        self,
        query: str,
        n_results: int = 10,
        filter_metadata: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """Query chunks by semantic similarity."""
        query_embedding = self.embed_text(query)
        return self._query_collection("chunks", query_embedding, n_results, filter_metadata)

    def delete_chunks_by_document(self, doc_id: int) -> bool:
        """Delete all chunks for a document."""
        vec_delete_by_metadata("chunks", "document_id", doc_id)
        return True

    def hybrid_search(
        self,
        query: str,
        n_results: int = 10,
        vector_weight: Optional[float] = None,
        text_weight: Optional[float] = None
    ) -> List[Dict[str, Any]]:
        """
        Perform hybrid search combining vector similarity and FTS5 BM25.

        Args:
            query: Search query
            n_results: Number of results to return
            vector_weight: Weight for vector scores (default from config)
            text_weight: Weight for BM25 scores (default from config)

        Returns:
            List of results with combined scores
        """
        try:
            from .db import search_chunks_fts, get_chunk_by_id
        except ImportError:
            from db import search_chunks_fts, get_chunk_by_id

        vector_weight = vector_weight or HYBRID_VECTOR_WEIGHT
        text_weight = text_weight or HYBRID_TEXT_WEIGHT

        # Get vector search results (more than needed for merging)
        try:
            vector_results = self.query_chunks(query, n_results=n_results * 2)
        except Exception as e:
            logger.warning(f"Vector search failed, using FTS only: {e}")
            vector_results = []

        # Get FTS5 BM25 results
        fts_results = search_chunks_fts(query, limit=n_results * 2)

        # Build score maps
        # Vector distance: lower is better, normalize to 0-1 score where higher is better
        vector_scores = {}
        if vector_results:
            max_dist = max(r['distance'] for r in vector_results) or 1
            min_dist = min(r['distance'] for r in vector_results) or 0
            dist_range = max_dist - min_dist or 1

            for r in vector_results:
                # Normalize: convert distance to similarity (1 - normalized_distance)
                normalized_dist = (r['distance'] - min_dist) / dist_range
                score = 1 - normalized_dist
                # Extract chunk_id from vector id (format: "chunk_N")
                chunk_id = r['id'].replace('chunk_', '') if r['id'].startswith('chunk_') else r['id']
                vector_scores[chunk_id] = {
                    'score': score,
                    'data': r
                }

        # BM25: lower is better in SQLite FTS5, normalize similarly
        bm25_scores = {}
        if fts_results:
            # BM25 scores are negative in SQLite, more negative = better match
            # Convert to positive scale where higher is better
            scores = [abs(r['bm25_score']) for r in fts_results]
            max_score = max(scores) or 1
            min_score = min(scores) or 0
            score_range = max_score - min_score or 1

            for r in fts_results:
                # Normalize and invert
                abs_score = abs(r['bm25_score'])
                normalized = (abs_score - min_score) / score_range
                score = 1 - normalized  # Higher original (less negative) = better
                chunk_id = str(r['id'])
                bm25_scores[chunk_id] = {
                    'score': score,
                    'data': r
                }

        # Merge scores
        all_chunk_ids = set(vector_scores.keys()) | set(bm25_scores.keys())
        combined = []

        for chunk_id in all_chunk_ids:
            vec_score = vector_scores.get(chunk_id, {}).get('score', 0)
            bm25_score = bm25_scores.get(chunk_id, {}).get('score', 0)

            final_score = (vec_score * vector_weight) + (bm25_score * text_weight)

            # Get chunk data from whichever source has it
            if chunk_id in vector_scores:
                data = vector_scores[chunk_id]['data']
                chunk_data = {
                    'chunk_id': chunk_id,
                    'content': data.get('document'),
                    'metadata': data.get('metadata', {}),
                    'vector_score': vec_score,
                    'bm25_score': bm25_score,
                    'combined_score': final_score
                }
            else:
                data = bm25_scores[chunk_id]['data']
                chunk_data = {
                    'chunk_id': chunk_id,
                    'content': data.get('content'),
                    'metadata': {
                        'document_id': data.get('document_id'),
                        'doc_title': data.get('doc_title'),
                        'doc_path': data.get('doc_path'),
                        'doc_type': data.get('doc_type'),
                        'start_line': data.get('start_line'),
                        'end_line': data.get('end_line')
                    },
                    'vector_score': vec_score,
                    'bm25_score': bm25_score,
                    'combined_score': final_score
                }

            combined.append(chunk_data)

        # Sort by combined score (higher is better)
        combined.sort(key=lambda x: x['combined_score'], reverse=True)

        return combined[:n_results]

    def index_document_chunks(
        self,
        document_id: int,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> List[str]:
        """
        Chunk a document and index all chunks.

        Args:
            document_id: The document ID in the database
            content: Full document content
            metadata: Additional metadata for all chunks

        Returns:
            List of chunk vector IDs
        """
        try:
            from .chunker import chunk_document, should_chunk
            from .db import add_chunk, delete_chunks_for_document, update_chunk_vector_id
        except ImportError:
            from chunker import chunk_document, should_chunk
            from db import add_chunk, delete_chunks_for_document, update_chunk_vector_id

        # Delete existing chunks for this document
        delete_chunks_for_document(document_id)
        self.delete_chunks_by_document(document_id)

        # Build path context prefix for searchability
        # This gets prepended to chunk content so both FTS5 and vector search
        # can match on file names, directory paths, and document titles.
        base_meta = metadata or {}
        path_context = _build_path_context(base_meta)

        # Check if document needs chunking
        if not should_chunk(content, CHUNK_THRESHOLD_TOKENS):
            # Small document - store as single chunk
            chunks = [{
                'text': content,
                'content_hash': '',
                'start_line': 1,
                'end_line': content.count('\n') + 1,
                'token_count': 0,
                'chunk_index': 0
            }]
        else:
            # Chunk the document
            chunk_objs = chunk_document(
                content,
                max_tokens=CHUNK_MAX_TOKENS,
                overlap_tokens=CHUNK_OVERLAP_TOKENS
            )
            chunks = [
                {
                    'text': c.text,
                    'content_hash': c.content_hash,
                    'start_line': c.start_line,
                    'end_line': c.end_line,
                    'token_count': c.token_count,
                    'chunk_index': c.chunk_index
                }
                for c in chunk_objs
            ]

        # Prepare batch for vector indexing
        batch = []
        db_chunk_ids = []

        for chunk in chunks:
            # Store chunk content with path context prefix in SQLite
            # so FTS5 can also match on file names and paths
            stored_content = path_context + chunk['text'] if path_context else chunk['text']

            # Add to SQLite
            chunk_id = add_chunk(
                document_id=document_id,
                content=stored_content,
                content_hash=chunk['content_hash'],
                start_line=chunk['start_line'],
                end_line=chunk['end_line'],
                chunk_index=chunk['chunk_index']
            )
            db_chunk_ids.append(chunk_id)

            # Prepare for vector store
            chunk_meta = base_meta.copy()
            chunk_meta['document_id'] = document_id
            chunk_meta['start_line'] = chunk['start_line']
            chunk_meta['end_line'] = chunk['end_line']
            chunk_meta['chunk_index'] = chunk['chunk_index']

            batch.append({
                'id': f"chunk_{chunk_id}",
                'content': stored_content,
                'metadata': chunk_meta
            })

        # Index all chunks in vector store
        vector_ids = self.add_chunks_batch(batch)

        # Update SQLite with vector IDs
        for db_id, vec_id in zip(db_chunk_ids, vector_ids):
            update_chunk_vector_id(db_id, vec_id)

        return vector_ids

    # ===========================================
    # FACTS OPERATIONS
    # ===========================================

    def add_fact(
        self,
        fact_id: str,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> str:
        """Add a fact to the vector store."""
        embedding = self.embed_text(content)

        meta = metadata or {}
        meta["indexed_at"] = datetime.now().isoformat()

        return self._add_to_collection("facts", fact_id, embedding, content, meta)

    def query_facts(
        self,
        query: str,
        n_results: int = 10,
        filter_metadata: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """Query facts by semantic similarity."""
        query_embedding = self.embed_text(query)
        return self._query_collection("facts", query_embedding, n_results, filter_metadata)

    # ===========================================
    # IDEAS OPERATIONS
    # ===========================================

    def add_idea(
        self,
        idea_id: str,
        content: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> str:
        """Add an idea to the vector store."""
        embedding = self.embed_text(content)

        meta = metadata or {}
        meta["indexed_at"] = datetime.now().isoformat()

        return self._add_to_collection("ideas", idea_id, embedding, content, meta)

    def query_ideas(
        self,
        query: str,
        n_results: int = 10,
        filter_metadata: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """Query ideas by semantic similarity."""
        query_embedding = self.embed_text(query)
        return self._query_collection("ideas", query_embedding, n_results, filter_metadata)

    def find_similar_ideas(self, idea_id: str, n_results: int = 5) -> List[Dict[str, Any]]:
        """Find ideas similar to a given idea."""
        blob = vec_get_embedding(idea_id, "ideas")
        if not blob:
            return []

        idea_embedding = _unpack_embedding(blob)

        # Query with its embedding, get extra to filter out self
        results = self._query_collection("ideas", idea_embedding, n_results + 1)

        # Filter out self
        filtered = [r for r in results if r['id'] != idea_id]
        return filtered[:n_results]

    # ===========================================
    # HEALTH OPERATIONS
    # ===========================================

    def add_health_entry(
        self,
        entry_id: str,
        summary: str,
        metadata: Optional[Dict[str, Any]] = None
    ) -> str:
        """Add a health entry summary to the vector store."""
        embedding = self.embed_text(summary)

        meta = metadata or {}
        meta["indexed_at"] = datetime.now().isoformat()

        return self._add_to_collection("health", entry_id, embedding, summary, meta)

    def query_health(
        self,
        query: str,
        n_results: int = 10,
        filter_metadata: Optional[Dict[str, Any]] = None
    ) -> List[Dict[str, Any]]:
        """Query health entries by semantic similarity."""
        query_embedding = self.embed_text(query)
        return self._query_collection("health", query_embedding, n_results, filter_metadata)

    # ===========================================
    # UNIFIED OPERATIONS
    # ===========================================

    def semantic_search(
        self,
        query: str,
        collections: Optional[List[str]] = None,
        n_results: int = 10
    ) -> Dict[str, List[Dict[str, Any]]]:
        """Search across multiple collections."""
        if collections is None:
            collections = list(VECTOR_COLLECTIONS.keys())

        results = {}
        query_embedding = self.embed_text(query)

        for coll_name in collections:
            try:
                formatted = self._query_collection(coll_name, query_embedding, n_results)
                results[coll_name] = formatted
            except Exception as e:
                # A failed collection is NOT the same as "no matches" -- log it at
                # WARNING with the exception class so a corrupt collection is visible
                # instead of silently looking empty.
                logger.warning(
                    "semantic_search: collection '%s' failed (%s): %s",
                    coll_name, type(e).__name__, e,
                )
                results[coll_name] = []

        return results

    def get_stats(self) -> Dict[str, int]:
        """Get counts for each collection."""
        stats = {}
        for coll_name in VECTOR_COLLECTIONS.keys():
            stats[coll_name] = vec_count(coll_name)
        return stats


# Singleton instance
_vault_vectors = None


def get_vault_vectors() -> VaultVectors:
    """Get the singleton VaultVectors instance. Always succeeds (SQLite always available)."""
    global _vault_vectors
    if _vault_vectors is None:
        _vault_vectors = VaultVectors()
    return _vault_vectors
