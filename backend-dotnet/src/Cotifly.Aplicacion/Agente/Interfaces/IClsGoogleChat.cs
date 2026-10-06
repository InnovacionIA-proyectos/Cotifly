using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.DTOs;

namespace Cotifly.Aplicacion.Agente.Interfaces;

/// <summary>
/// Publica mensajes y tarjetas en un hilo de Google Chat (port de <c>PublicadorChat</c> de <c>orquestador/publicador.py</c>):
/// <c>spaces.messages.create</c> con la cuenta de servicio de la app.
/// </summary>
public interface IClsPublicadorChat
{
    /// <summary>
    /// Publica <paramref name="mensaje"/> (texto o <c>cardsV2</c>) en <paramref name="spaceId"/>; con <paramref name="threadId"/>
    /// responde en ese hilo (si ya no existe, abre uno nuevo).
    /// </summary>
    Task PublicarAsync(string spaceId, string? threadId, JsonObject mensaje, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cola en memoria entre el webhook de Google Chat y el worker (port de <c>ColaEventos</c> de <c>orquestador/worker.py</c>): el
/// webhook acusa en menos de 30 s sin esperar al agente.
/// </summary>
public interface IClsColaEventosChat
{
    /// <summary>Encola el evento sin esperar. <c>false</c> si la cola está llena (el webhook igual acusa).</summary>
    bool Encolar(clsEventoChat evento);

    /// <summary>Siguiente evento (espera si la cola está vacía). Lo consume el worker.</summary>
    ValueTask<clsEventoChat> LeerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Verificación del JWT que firma Google Chat en cada llamada al webhook (port de <c>GoogleChatJwtVerifier</c>): firma RS256 con
/// los certificados públicos de <c>chat@system.gserviceaccount.com</c>, audiencia = número del proyecto (<c>GChat:ProjectNumber</c>)
/// y emisor de sistema de Chat.
/// </summary>
public interface IClsVerificadorJwtGoogleChat
{
    /// <summary>
    /// <c>true</c> si el encabezado <c>Authorization: Bearer &lt;jwt&gt;</c> es válido, o si la verificación está desactivada
    /// (<c>GChat:VerificarJwt=false</c>). Nunca lanza por un token inválido.
    /// </summary>
    Task<bool> VerificarAsync(string? authorization, CancellationToken cancellationToken = default);
}

/// <summary>
/// Candado de un turno por sesión (auditoría de #12, riesgo 4): dos mensajes simultáneos de la misma conversación no corren a la vez
/// (se pisarían el estado y el historial). En SQL Server: <c>sp_getapplock</c> con dueño <c>Session</c> sobre una conexión propia,
/// recurso <c>cotifly:sesion:{spaceId}:{threadId}</c>; vale entre procesos y réplicas.
/// </summary>
public interface IClsCandadoSesion
{
    /// <summary>
    /// Toma el candado esperando como máximo <paramref name="espera"/>. Devuelve el candado tomado (se libera al desecharlo) o
    /// <c>null</c> si otro turno de la misma sesión sigue en curso.
    /// </summary>
    Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default);
}
