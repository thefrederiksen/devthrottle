"""
Fuzzy and phonetic name matching for contact search.

Uses a hybrid multi-algorithm scoring system:
- Metaphone: Sound-alike matching (Smith/Smyth)
- Jaro-Winkler: Typos, transpositions
- Token Set Ratio: Partial/reordered names

Combined score formula:
    score = (phonetic * 0.3) + (jaro_winkler * 0.4) + (token_set * 0.3)

Phonetic matching uses jellyfish.metaphone (single Metaphone). jellyfish does
not provide a Double Metaphone, so primary and secondary codes are identical;
the (primary, secondary) tuple shape is kept for callers but both slots hold the
same Metaphone code.
"""

from typing import List, Dict, Any, Optional, Tuple
import jellyfish
from rapidfuzz import fuzz


# Common nicknames mapping (nickname -> full names)
NICKNAME_MAP = {
    'mike': ['michael'],
    'michael': ['mike'],
    'jon': ['jonathan', 'john'],
    'john': ['jonathan', 'jon'],
    'jonathan': ['john', 'jon'],
    'bob': ['robert'],
    'robert': ['bob', 'rob', 'bobby'],
    'rob': ['robert'],
    'bobby': ['robert'],
    'bill': ['william'],
    'william': ['bill', 'will', 'billy'],
    'will': ['william'],
    'billy': ['william'],
    'dick': ['richard'],
    'richard': ['dick', 'rick', 'ricky'],
    'rick': ['richard'],
    'ricky': ['richard'],
    'jim': ['james'],
    'james': ['jim', 'jimmy'],
    'jimmy': ['james'],
    'joe': ['joseph'],
    'joseph': ['joe', 'joey'],
    'joey': ['joseph'],
    'tom': ['thomas'],
    'thomas': ['tom', 'tommy'],
    'tommy': ['thomas'],
    'dave': ['david'],
    'david': ['dave', 'davy'],
    'davy': ['david'],
    'dan': ['daniel'],
    'daniel': ['dan', 'danny'],
    'danny': ['daniel'],
    'chris': ['christopher', 'christian'],
    'christopher': ['chris'],
    'christian': ['chris'],
    'matt': ['matthew'],
    'matthew': ['matt'],
    'nick': ['nicholas'],
    'nicholas': ['nick', 'nicky'],
    'nicky': ['nicholas'],
    'steve': ['steven', 'stephen'],
    'steven': ['steve'],
    'stephen': ['steve'],
    'tony': ['anthony'],
    'anthony': ['tony'],
    'ed': ['edward', 'edwin'],
    'edward': ['ed', 'eddie', 'ted'],
    'eddie': ['edward'],
    'ted': ['edward', 'theodore'],
    'theodore': ['ted', 'teddy'],
    'teddy': ['theodore'],
    'greg': ['gregory'],
    'gregory': ['greg'],
    'alex': ['alexander', 'alexandra'],
    'alexander': ['alex'],
    'alexandra': ['alex'],
    'liz': ['elizabeth'],
    'elizabeth': ['liz', 'beth', 'betty', 'lizzy'],
    'beth': ['elizabeth'],
    'betty': ['elizabeth'],
    'lizzy': ['elizabeth'],
    'kate': ['katherine', 'catherine'],
    'katherine': ['kate', 'kathy', 'katie'],
    'catherine': ['kate', 'cathy', 'katie'],
    'kathy': ['katherine'],
    'cathy': ['catherine'],
    'katie': ['katherine', 'catherine'],
    'jen': ['jennifer'],
    'jennifer': ['jen', 'jenny'],
    'jenny': ['jennifer'],
    'sue': ['susan'],
    'susan': ['sue', 'suzy'],
    'suzy': ['susan'],
    'sam': ['samuel', 'samantha'],
    'samuel': ['sam'],
    'samantha': ['sam'],
    'pat': ['patrick', 'patricia'],
    'patrick': ['pat', 'paddy'],
    'patricia': ['pat', 'patty', 'tricia'],
    'paddy': ['patrick'],
    'patty': ['patricia'],
    'tricia': ['patricia'],
    'ben': ['benjamin'],
    'benjamin': ['ben'],
    'tim': ['timothy'],
    'timothy': ['tim'],
    'andy': ['andrew'],
    'andrew': ['andy', 'drew'],
    'drew': ['andrew'],
    'pete': ['peter'],
    'peter': ['pete'],
    'phil': ['phillip', 'philip'],
    'phillip': ['phil'],
    'philip': ['phil'],
    'fred': ['frederick'],
    'frederick': ['fred', 'freddy'],
    'freddy': ['frederick'],
    'frank': ['francis', 'franklin'],
    'francis': ['frank'],
    'franklin': ['frank'],
    'jack': ['john', 'jackson'],
    'jackson': ['jack'],
    'charlie': ['charles'],
    'charles': ['charlie', 'chuck'],
    'chuck': ['charles'],
    'harry': ['harold', 'henry'],
    'harold': ['harry'],
    'henry': ['harry', 'hank'],
    'hank': ['henry'],
}


def compute_metaphone(name: str) -> Tuple[str, str]:
    """
    Generate Metaphone codes for a name.

    Returns (primary, secondary) phonetic codes. jellyfish only provides a single
    Metaphone, so secondary == primary; the tuple shape is kept for callers.
    """
    if not name:
        return ('', '')
    # Clean the name
    clean_name = ''.join(c for c in name.lower() if c.isalpha() or c.isspace())
    if not clean_name.strip():
        return ('', '')
    # Get metaphone for each word and concatenate
    words = clean_name.split()
    codes = []
    for word in words:
        if word:
            codes.append(jellyfish.metaphone(word))
    joined = ' '.join(codes)
    return (joined, joined)


def _phonetic_similarity(query: str, target: str) -> float:
    """
    Calculate phonetic similarity between query and target.
    Returns score 0-100.
    """
    if not query or not target:
        return 0.0

    q_primary, _ = compute_metaphone(query)
    t_primary, _ = compute_metaphone(target)

    if not q_primary or not t_primary:
        return 0.0

    # Check for exact phonetic match
    if q_primary == t_primary:
        return 100.0

    # Check for partial phonetic match (token matching)
    q_tokens = set(q_primary.split())
    t_tokens = set(t_primary.split())

    if q_tokens and t_tokens:
        intersection = len(q_tokens & t_tokens)
        union = len(q_tokens | t_tokens)
        if union > 0:
            return (intersection / union) * 100.0

    return 0.0


def _nickname_match(query: str, name: str, nickname: Optional[str]) -> float:
    """
    Check if query matches known nicknames for the contact's name.
    Returns score 0-100.
    """
    query_lower = query.lower().strip()
    name_lower = name.lower().strip() if name else ''
    nickname_lower = nickname.lower().strip() if nickname else ''

    # Get first name from full name
    first_name = name_lower.split()[0] if name_lower else ''

    # Direct match on nickname field
    if nickname_lower and query_lower == nickname_lower:
        return 100.0

    # Check if query is a known nickname for the first name
    if first_name in NICKNAME_MAP:
        if query_lower in NICKNAME_MAP[first_name]:
            return 95.0

    # Check if first name is a known nickname for the query
    if query_lower in NICKNAME_MAP:
        if first_name in NICKNAME_MAP[query_lower]:
            return 95.0

    return 0.0


def score_contact(query: str, contact: Dict[str, Any]) -> Dict[str, Any]:
    """
    Score a contact match against a query.
    Returns dict with:
        - total_score: Combined weighted score (0-100)
        - match_type: 'exact', 'fuzzy', or 'phonetic'
        - details: Dict of individual algorithm scores
    """
    name = contact.get('name', '') or ''
    nickname = contact.get('nickname', '') or ''

    if not query or not name:
        return {
            'total_score': 0,
            'match_type': 'none',
            'details': {}
        }

    query_lower = query.lower().strip()
    name_lower = name.lower().strip()

    # Check for exact match first
    if query_lower == name_lower or query_lower in name_lower:
        return {
            'total_score': 100,
            'match_type': 'exact',
            'details': {'exact': 100}
        }

    # Compute individual scores
    # 1. Phonetic similarity (Double Metaphone)
    phonetic_score = _phonetic_similarity(query, name)

    # 2. Jaro-Winkler (good for typos)
    jaro_winkler_score = jellyfish.jaro_winkler_similarity(query_lower, name_lower) * 100

    # 3. Token Set Ratio (handles partial/reordered names)
    token_set_score = fuzz.token_set_ratio(query_lower, name_lower)

    # 4. Nickname matching (bonus)
    nickname_score = _nickname_match(query, name, nickname)

    # Calculate weighted score
    # Weights: phonetic=0.3, jaro_winkler=0.4, token_set=0.3
    base_score = (
        (phonetic_score * 0.3) +
        (jaro_winkler_score * 0.4) +
        (token_set_score * 0.3)
    )

    # Add nickname bonus (up to 20 points)
    if nickname_score > 0:
        total_score = min(100, base_score + (nickname_score * 0.2))
    else:
        total_score = base_score

    # Determine match type
    if phonetic_score >= 80:
        match_type = 'phonetic'
    else:
        match_type = 'fuzzy'

    return {
        'total_score': round(total_score, 1),
        'match_type': match_type,
        'details': {
            'phonetic': round(phonetic_score, 1),
            'jaro_winkler': round(jaro_winkler_score, 1),
            'token_set': round(token_set_score, 1),
            'nickname': round(nickname_score, 1)
        }
    }


def fuzzy_search_contacts(
    query: str,
    contacts: List[Dict[str, Any]],
    threshold: int = 50,
    limit: int = 10
) -> List[Dict[str, Any]]:
    """
    Search contacts using fuzzy and phonetic matching.

    Args:
        query: Search query (name to find)
        contacts: List of contact dicts (must have 'name' field)
        threshold: Minimum score (0-100) to include in results
        limit: Maximum number of results to return

    Returns:
        List of contacts with match scores, sorted by score descending.
        Each contact dict has added 'match_score' and 'match_type' fields.
    """
    if not query or not contacts:
        return []

    results = []

    for contact in contacts:
        score_result = score_contact(query, contact)
        total_score = score_result['total_score']

        if total_score >= threshold:
            # Create result with score info
            result = dict(contact)
            result['match_score'] = total_score
            result['match_type'] = score_result['match_type']
            result['match_details'] = score_result['details']
            results.append(result)

    # Sort by score descending
    results.sort(key=lambda x: x['match_score'], reverse=True)

    # Apply limit
    return results[:limit]
