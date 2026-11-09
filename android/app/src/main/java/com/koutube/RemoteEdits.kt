package com.koutube

/** Retrying an older request must never overwrite newer local progress. */
fun mergeRemoteEdit(old: RemoteEdit?, next: RemoteEdit): RemoteEdit {
    if (old == null) return next
    return next.copy(
        watch = listOfNotNull(old.watch, next.watch).maxByOrNull { it.ts },
        flags = listOfNotNull(old.flags, next.flags).maxByOrNull { it.ts },
        delete = old.delete || next.delete,
    )
}
