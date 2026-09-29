using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>Identidad que pasó la verificación doble.</summary>
/// <param name="Usuario">Usuario de <c>UsuariosWeb</c> (seguido por el contexto: si es nuevo, falta confirmarlo).</param>
/// <param name="Rol">Rol interno resuelto (igual al asignado).</param>
/// <param name="EsNuevo"><c>true</c> si se creó en esta verificación (primer ingreso, JIT).</param>
public sealed record clsIdentidadVerificada(clsUsuarioWeb Usuario, RolInterno Rol, bool EsNuevo);

/// <summary>Resultado de la verificación: la identidad o el motivo del rechazo, más la regla de rol aplicada.</summary>
/// <param name="UsuarioIdRechazado">
/// Id interno (<c>UsuariosWeb.id</c>) del usuario rechazado, cuando ya existe (p. ej. <c>rol_externo_cambio</c>): va a la auditoría para que
/// el gerente o el administrador lo revise (auditoría de #12).
/// </param>
public sealed record clsVerificacionIdentidad(clsIdentidadVerificada? Identidad, string Motivo, string? Regla, Guid? UsuarioIdRechazado = null)
{
    public bool EsValida => Identidad is not null;
}

/// <summary>
/// Verificación DOBLE de la identidad (ADR-009, decisión del 2026-09-25), común al login y al refresh:
/// <list type="number">
/// <item><b>Externa</b> (almacén corporativo, solo lectura): la persona existe y sus roles de ClientePro, traducidos con
/// <c>Roles.idRolExterno</c>, dan <c>comercial</c> o <c>gerente_comercial</c>. Con ambos gana <c>gerente_comercial</c>
/// (regla <see cref="clsMotivosAcceso.ReglaPrecedenciaGerente"/>, auditada).</item>
/// <item><b>Interna</b> (<c>dbo_cotifly</c>): el <c>NOMBREUSUARIO</c> del almacén (nunca el del request) ubica al usuario.
/// Si no existe y el ID externo tampoco está vinculado a otra cuenta, se crea (JIT) con su asignación en
/// <c>UsuariosRoles</c>. Si existe, su asignación activa debe ser el rol resuelto y su vínculo, el mismo ID externo.</item>
/// </list>
/// Cualquier diferencia de rol o de <c>NOMBREUSUARIO</c> es un rechazo con motivo; eso nunca se re-sincroniza solo. Los fallos
/// del almacén son <see cref="clsMotivosAcceso.AlmacenNoDisponible"/> (fail-closed). Los fallos de <c>dbo_cotifly</c> se propagan.
/// <para>
/// <b>Datos corporativos (#81, solo en el login):</b> la regional se busca en <c>DimEjecutivo</c>
/// (<see cref="IClsDirectorioEjecutivos"/>) al crear el usuario y en cada login siguiente ya verificado, que además refresca
/// nombre y correo desde el almacén (<see cref="clsUsuarioWeb.ActualizarDatos"/>). Solo se sobrescribe con valores no vacíos:
/// un usuario que no aparece en <c>DimEjecutivo</c> conserva la regional que tenga (la del DBA, o NULL). Esa búsqueda nunca
/// bloquea el login: sin conexión no se consulta, y si falla o supera <see cref="IClsDirectorioEjecutivos.LimiteLogin"/> se
/// registra un warning y el login sigue con la regional que ya tenía.
/// </para>
/// </summary>
public sealed class clsVerificadorIdentidad(
    IClsAlmacenIdentidadExterno almacen,
    IClsRolRepository roles,
    IClsUsuarioWebRepository usuarios,
    IClsDirectorioEjecutivos directorio,
    ILogger<clsVerificadorIdentidad> logger) : IClsVerificadorIdentidad
{
    /// <summary>Límite de la búsqueda de la regional si el directorio no da uno válido.</summary>
    public static readonly TimeSpan LimiteLoginPorDefecto = TimeSpan.FromSeconds(3);

    /// <param name="idUsuario">Identidad a revalidar (como <c>NOMBREUSUARIO</c> y, si no, como <c>ID</c>).</param>
    /// <param name="permitirAlta">
    /// <c>true</c> en el login: alta JIT y refresco de los datos corporativos (nombre, correo y regional). <c>false</c> en el
    /// refresh y en Google Chat: el usuario ya debe existir y no se toca.
    /// </param>
    /// <param name="ahora">Instante de la operación.</param>
    /// <param name="cancellationToken">Cancelación del llamador.</param>
    public async Task<clsVerificacionIdentidad> VerificarAsync(
        string idUsuario, bool permitirAlta, DateTimeOffset ahora, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idUsuario);

        clsUsuarioExterno? externo;
        IReadOnlyList<clsRolExterno> rolesExternos;
        try
        {
            externo = await almacen.BuscarUsuarioAsync(idUsuario, cancellationToken);
            if (externo is null)
            {
                return Rechazo(clsMotivosAcceso.UsuarioNoEncontrado);
            }

            rolesExternos = await almacen.ObtenerRolesAsync(externo.IdUsuarioExterno, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Almacén corporativo de identidad no disponible ({Tipo}): {Mensaje}", ex.GetType().Name, ex.Message);
            logger.LogDebug(ex, "Detalle del fallo del almacén corporativo de identidad.");
            return Rechazo(clsMotivosAcceso.AlmacenNoDisponible);
        }

        var catalogo = await roles.ListarActivosAsync(cancellationToken);
        var (rolResuelto, regla) = ResolverRol(catalogo, rolesExternos);
        if (rolResuelto is not { } rol)
        {
            return Rechazo(clsMotivosAcceso.SinRolAutorizado);
        }

        var porNombre = await usuarios.ObtenerPorNombreUsuarioAsync(externo.NombreUsuario, cancellationToken);
        if (porNombre is null)
        {
            if (await usuarios.ObtenerPorIdUsuarioExternoAsync(externo.IdUsuarioExterno, cancellationToken) is not null)
            {
                // Mismo ID del almacén con otro NOMBREUSUARIO: no se crea una cuenta duplicada; se revisa a mano.
                return Rechazo(clsMotivosAcceso.NombreUsuarioCambio, regla);
            }

            if (!permitirAlta)
            {
                return Rechazo(clsMotivosAcceso.UsuarioNoEncontrado, regla);
            }

            var regional = await BuscarRegionalAsync(externo.NombreUsuario, cancellationToken);
            var nuevo = clsUsuarioWeb.Crear(
                externo.NombreUsuario, externo.NombreCompleto, externo.Email, regional, ahora, externo.IdUsuarioExterno);
            var asignacion = clsUsuarioRol.Asignar(nuevo.Valor.Id, catalogo.First(r => r.Codigo == rol).Id, ahora);
            usuarios.Agregar(nuevo.Valor);
            roles.AgregarAsignacion(asignacion.Valor);
            return new clsVerificacionIdentidad(new clsIdentidadVerificada(nuevo.Valor, rol, EsNuevo: true), clsMotivosAcceso.AltaJit, regla);
        }

        if (porNombre.IdUsuarioExterno is { } vinculado && vinculado != externo.IdUsuarioExterno)
        {
            // El nombre ahora pertenece a otra fila del almacén.
            return Rechazo(clsMotivosAcceso.IdentidadInconsistente, regla, porNombre.Id);
        }

        var asignados = await roles.ListarRolesAsignadosAsync(porNombre.Id, cancellationToken);
        if (asignados.Count == 0)
        {
            return Rechazo(clsMotivosAcceso.SinAsignacionActiva, regla, porNombre.Id);
        }

        if (asignados.Count != 1 || asignados[0] != rol)
        {
            return Rechazo(clsMotivosAcceso.RolExternoCambio, regla, porNombre.Id);
        }

        if (porNombre.IdUsuarioExterno is null)
        {
            porNombre.VincularUsuarioExterno(externo.IdUsuarioExterno);
        }

        if (permitirAlta)
        {
            // Login ya verificado (rol y NOMBREUSUARIO coinciden): solo se refrescan datos que no deciden el acceso.
            var regional = await BuscarRegionalAsync(externo.NombreUsuario, cancellationToken);
            porNombre.ActualizarDatos(externo.NombreCompleto, externo.Email, regional);
        }

        return new clsVerificacionIdentidad(new clsIdentidadVerificada(porNombre, rol, EsNuevo: false), clsMotivosAcceso.UsuarioExistente, regla);
    }

    /// <summary>
    /// Traduce los roles externos con el catálogo (<c>Roles.idRolExterno</c>, solo roles activos y mapeados). Sin ninguno:
    /// <c>null</c>. Con los dos roles internos gana <c>gerente_comercial</c> (regla explícita y auditada; si luego pierde el
    /// rol de gerente, su asignación deja de coincidir y queda en revisión).
    /// </summary>
    public static (RolInterno? Rol, string? Regla) ResolverRol(IEnumerable<clsRol> catalogo, IEnumerable<clsRolExterno> rolesExternos)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        ArgumentNullException.ThrowIfNull(rolesExternos);

        var externos = rolesExternos.Select(r => r.IdRol).ToHashSet();
        var mapeados = catalogo
            .Where(r => r.Activo && r.IdRolExterno is { } id && externos.Contains(id))
            .Select(r => r.Codigo)
            .Distinct()
            .ToList();

        return mapeados.Count switch
        {
            0 => (null, null),
            1 => (mapeados[0], clsMotivosAcceso.ReglaMapeoUnico),
            _ => (RolInterno.GerenteComercial, clsMotivosAcceso.ReglaPrecedenciaGerente),
        };
    }

    /// <summary>
    /// Regional de <paramref name="nombreUsuario"/> en <c>DimEjecutivo</c>, o <c>null</c> si no está configurada, el usuario no
    /// aparece (o aparece con dos regionales) o la fuente falla o tarda más de <see cref="IClsDirectorioEjecutivos.LimiteLogin"/>.
    /// Nunca lanza, salvo la cancelación del llamador. El log solo lleva el nombre de usuario y el tipo de error.
    /// </summary>
    private async Task<string?> BuscarRegionalAsync(string nombreUsuario, CancellationToken cancellationToken)
    {
        if (!directorio.Configurado)
        {
            return null;
        }

        var limite = directorio.LimiteLogin > TimeSpan.Zero ? directorio.LimiteLogin : LimiteLoginPorDefecto;
        using var tiempo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tiempo.CancelAfter(limite);
        Task<IReadOnlyDictionary<string, string>>? consulta = null;
        try
        {
            // WaitAsync corta la espera aunque el cliente SQL tarde en atender la cancelación (p. ej. al abrir la conexión).
            consulta = directorio.ObtenerRegionalesAsync([nombreUsuario], tiempo.Token);
            var mapa = await consulta.WaitAsync(limite, cancellationToken);
            var clave = nombreUsuario.Trim();
            var regional = mapa.TryGetValue(clave, out var exacta)
                ? exacta
                : mapa.FirstOrDefault(p => string.Equals(p.Key, clave, StringComparison.OrdinalIgnoreCase)).Value;
            return string.IsNullOrWhiteSpace(regional) ? null : regional.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "No se pudo consultar la regional de '{NombreUsuario}' en DimEjecutivo ({Tipo}); el login sigue con la regional guardada.",
                nombreUsuario, ex.GetType().Name);
            logger.LogDebug(ex, "Detalle del fallo de la consulta de la regional en el login.");
            return null;
        }
        finally
        {
            if (consulta is { IsCompleted: false })
            {
                // La consulta abandonada se cancela y su error, si llega, se observa aquí (no queda como excepción sin observar).
                await tiempo.CancelAsync();
                _ = consulta.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    private static clsVerificacionIdentidad Rechazo(string motivo, string? regla = null, Guid? usuarioId = null) => new(null, motivo, regla, usuarioId);
}

/// <summary>
/// Verificación doble de la identidad (login, refresh y, desde #12 parte C, cada turno de Google Chat). Interfaz para poder
/// simularla en las pruebas del procesamiento de Google Chat.
/// </summary>
public interface IClsVerificadorIdentidad
{
    Task<clsVerificacionIdentidad> VerificarAsync(string idUsuario, bool permitirAlta, DateTimeOffset ahora, CancellationToken cancellationToken = default);
}
