package com.koutube

import android.net.Uri
import android.provider.DocumentsContract
import android.provider.DocumentsContract.Document
import java.io.FileInputStream
import java.io.FileOutputStream
import java.nio.ByteBuffer

class Doc(val id: String, val name: String, val size: Long, val mtime: Long, val isDir: Boolean)

/**
 * One storage tree picked through the system folder picker. The same code serves a USB stick,
 * an SD card and a folder in internal memory — SAF is the only route to a USB stick that works
 * on every Android version, so everything goes through it. Calls throw when the medium is gone;
 * callers treat that as "storage offline".
 */
class Saf(val tree: Uri) {
    private val cr = App.instance.contentResolver
    val rootId: String = DocumentsContract.getTreeDocumentId(tree)

    fun uri(docId: String): Uri = DocumentsContract.buildDocumentUriUsingTree(tree, docId)

    fun list(dirId: String): List<Doc> {
        val u = DocumentsContract.buildChildDocumentsUriUsingTree(tree, dirId)
        val cols = arrayOf(Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME, Document.COLUMN_SIZE,
            Document.COLUMN_LAST_MODIFIED, Document.COLUMN_MIME_TYPE)
        val out = ArrayList<Doc>()
        cr.query(u, cols, null, null, null)?.use { c ->
            while (c.moveToNext()) out += Doc(c.getString(0), c.getString(1) ?: "", c.getLong(2), c.getLong(3),
                c.getString(4) == Document.MIME_TYPE_DIR)
        } ?: error("storage unavailable")
        return out
    }

    fun child(dirId: String, name: String) = list(dirId).firstOrNull { it.name == name }

    /** Folder by path relative to the root ("." is the root itself), or null. */
    fun resolveDir(path: String): String? {
        var id = rootId
        for (part in path.split('/').filter { it.isNotEmpty() && it != "." }) {
            id = list(id).firstOrNull { it.isDir && it.name == part }?.id ?: return null
        }
        return id
    }

    fun dir(parentId: String, name: String): String = child(parentId, name)?.takeIf { it.isDir }?.id
        ?: DocumentsContract.createDocument(cr, uri(parentId), Document.MIME_TYPE_DIR, name)?.let { DocumentsContract.getDocumentId(it) }
        ?: error("cannot create $name")

    fun read(docId: String): ByteArray = cr.openInputStream(uri(docId))!!.use { it.readBytes() }

    /** Creates or truncates [name] in [parentId] and syncs it to the medium. */
    fun write(parentId: String, name: String, bytes: ByteArray): String {
        val id = child(parentId, name)?.id
            ?: DocumentsContract.createDocument(cr, uri(parentId), "application/octet-stream", name)?.let { DocumentsContract.getDocumentId(it) }
            ?: error("cannot create $name")
        cr.openFileDescriptor(uri(id), "wt")!!.use { pfd ->
            FileOutputStream(pfd.fileDescriptor).use { it.write(bytes); it.flush(); it.fd.sync() }
        }
        return id
    }

    fun rename(docId: String, name: String): String =
        DocumentsContract.renameDocument(cr, uri(docId), name)?.let { DocumentsContract.getDocumentId(it) } ?: docId

    fun delete(docId: String): Boolean = DocumentsContract.deleteDocument(cr, uri(docId))

    /** Safe replace (FORMAT.md §4): write X.tmp, move X to X.bak, move X.tmp to X. */
    fun writeAtomic(dirId: String, name: String, bytes: ByteArray) {
        val tmp = write(dirId, "$name.tmp", bytes)
        val kids = list(dirId)
        kids.firstOrNull { it.name == name }?.let { cur ->
            kids.firstOrNull { it.name == "$name.bak" }?.let { delete(it.id) }
            rename(cur.id, "$name.bak")
        }
        rename(tmp, name)
    }

    /** Every verified copy of [name] (X, X.tmp, X.bak) — the caller picks the newest. */
    fun readCandidates(dirId: String, name: String): List<String> = list(dirId)
        .filter { it.name == name || it.name == "$name.tmp" || it.name == "$name.bak" }
        .mapNotNull { runCatching { unseal(String(read(it.id))) }.getOrNull() }

    fun fileKey(doc: Doc): String = cr.openFileDescriptor(uri(doc.id), "r")!!.use { pfd ->
        FileInputStream(pfd.fileDescriptor).channel.use { ch ->
            val size = ch.size()
            fun chunk(at: Long, n: Int): ByteArray {
                val b = ByteBuffer.allocate(n); ch.position(at)
                while (b.hasRemaining() && ch.read(b) > 0) Unit
                return b.array().copyOf(b.position())
            }
            if (size < 2 * KEY_CHUNK) fileKey(size, chunk(0, size.toInt()), ByteArray(0))
            else fileKey(size, chunk(0, KEY_CHUNK), chunk(size - KEY_CHUNK, KEY_CHUNK))
        }
    }
}
