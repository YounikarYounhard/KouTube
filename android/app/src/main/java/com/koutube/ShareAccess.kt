package com.koutube

import kotlinx.serialization.Serializable

@Serializable data class ShareFolder(val storageId: String, val path: String)

fun canReadFolder(grants: List<ShareFolder>, storageId: String, path: String?): Boolean {
    if (path == null || path.startsWith('/') || '\\' in path || path.split('/').any { it == ".." || it.isEmpty() }) return false
    return grants.any { it.storageId == storageId && (it.path == "." || path.startsWith(it.path.trimEnd('/') + "/")) }
}
