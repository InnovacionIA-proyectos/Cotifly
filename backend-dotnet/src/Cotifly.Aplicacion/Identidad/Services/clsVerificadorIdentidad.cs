using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>Identidad que pasó la verificación doble.</summary>
/// <param name="Usuario">Usuario de <c>UsuariosWeb</c> (seguido por el contexto: si es nuevo, falta confirmarlo).</param>
/// <param name="Rol">
/// Rol interno de la jerarquía (<c>director</c> &gt; <c>gerente_comercial</c> &gt; <c>comercial</c>). Con un solo rol disponible es el
/// rol de la sesión; con varios (<see cref="RequiereEleccion"/>) NO se usa para emitir: la persona elige (HU-040.2, ADR-025).
/// </param>
/// <param name="EsNuevo"><c>true</c> si se creó en esta verificación (primer ingreso, JIT).</param>
/// <param name="RolesDisponibles">
/// HU-040.2 (#230): roles con los que puede entrar, del catálogo y en el orden de la jerarquía. Uno (<see cref="Rol"/>) en el caso de
/// rol único; varios si tiene dos o más asignaciones activas (todas respaldadas por ClientePro) o si es un alta con varios roles externos.
/// </param>
/// <param name="RolesOtorgados">
/// HU-040.3 (#258): roles ORDINARIOS que la reconciliación del login auto-otorgó en esta verificación (faltaban y tenían respaldo externo;
/// se creó o reactivó la asignación). Vacío fuera del login o cuando no hubo cambios. Solo para la auditoría.
/// </param>
/// <param name="RolesRevocados">
/// HU-040.3 (#258): roles que la reconciliación del login auto-revocó (su rol externo dejó de respaldarlos; aplica a todos, fail-safe).
/// Vacío fuera del login o cuando no hubo cambios. Solo para la auditoría.
/// </param>
public sealed record clsIdentidadVerificada(
    clsUsuarioWeb Usuario, RolInterno Rol, bool EsNuevo, IReadOnlyList<clsRol> RolesDisponibles,
    IReadOnlyList<RolInterno>? RolesOtorgados = null, IReadOnlyList<RolInterno>? RolesRevocados = null)
{
    /// <summary>HU-040.2 (#230): con varios roles disponibles el login no emite sesión: la persona elige con cuál entra.</summary>
    public bool RequiereEleccion => RolesDisponibles.Count > 1;

    /// <summary>HU-040.3 (#258): roles ordinarios auto-otorgados en el login (nunca <c>null</c>).</summary>
    public IReadOnlyList<RolInterno> Otorgados => RolesOtorgados ?? [];

    /// <summary>HU-040.3 (#258): roles auto-revocados en el login (nunca <c>null</c>).</summary>
    public IReadOnlyList<RolInterno> Revocados => RolesRevocados ?? [];

    /// <summary><c>true</c> si <paramref name="rol"/> está entre <see cref="RolesDisponibles"/>.</summary>
    public bool PuedeEntrarCon(RolInterno rol) => RolesDisponibles.Any(r => r.Codigo == rol);
}

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
/// <c>Roles.idRolExterno</c>, dan <c>comercial</c>, <c>gerente_comercial</c> o <c>director</c>. Con varios gana el de mayor alcance,
/// <c>director</c> &gt; <c>gerente_comercial</c> &gt; <c>comercial</c> (reglas <see cref="clsMotivosAcceso.ReglaPrecedenciaDirector"/> y
/// <see cref="clsMotivosAcceso.ReglaPrecedenciaGerente"/>, auditadas; HU-037, #210).</item>
/// <item><b>Interna</b> (<c>dbo_cotifly</c>): el <c>NOMBREUSUARIO</c> del almacén (nunca el del request) ubica al usuario.
/// Si no existe y el ID externo tampoco está vinculado a otra cuenta, se crea (JIT) con su asignación en
/// <c>UsuariosRoles</c> (HU-040.2: una por cada rol externo mapeado). Si existe, el rol de la jerarquía debe estar entre sus asignaciones
/// activas, cada asignación activa debe seguir respaldada por un rol externo vigente y su vínculo debe ser el mismo ID externo. HU-040.1
/// (#229, script 16): el esquema admite varias asignaciones activas; con una sola, la regla es la de siempre (la asignación es exactamente
/// el rol resuelto).</item>
/// <item><b>Roles disponibles</b> (HU-040.2, #230, ADR-025): con una asignación activa, el rol de la jerarquía (rol único, igual que
/// antes); con dos o más, todas ellas (regla <see cref="clsMotivosAcceso.ReglaMultiRol"/>): la persona elige al entrar.</item>
/// </list>
/// HU-040.3 (#258, ADR-009 OPCIÓN B+): <b>en el login</b> (<c>permitirAlta: true</c>) el rol interno se RECONCILIA con los roles externos
/// vigentes: se auto-otorgan los roles ORDINARIOS que falten y se auto-revocan TODOS los que perdieron su respaldo (fail-safe); un rol
/// SENSIBLE (<see cref="clsRol.RequiereAltaManual"/>) que falte sigue con rechazo + alta manual. <b>En el refresh y en la elección de rol</b>
/// (<c>permitirAlta: false</c>) la verificación sigue ESTRICTA: cualquier desajuste de rol es un rechazo, no se reconcilia. Un
/// <c>NOMBREUSUARIO</c> inconsistente siempre es rechazo. Los fallos del almacén son <see cref="clsMotivosAcceso.AlmacenNoDisponible"/>
/// (fail-closed, nunca se reconcilia sobre una lectura fallida). Los fallos de <c>dbo_cotifly</c> se propagan.
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
    /// refresh: el usuario ya debe existir y no se toca.
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
        var mapeados = RolesMapeados(catalogo, rolesExternos);
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
            usuarios.Agregar(nuevo.Valor);
            // HU-040.2 (#230): el alta asigna TODOS los roles externos mapeados (multi-rol desde el primer ingreso); con uno solo, uno.
            var disponiblesAlta = Disponibles(catalogo, mapeados);
            foreach (var disponible in disponiblesAlta)
            {
                roles.AgregarAsignacion(clsUsuarioRol.Asignar(nuevo.Valor.Id, disponible.Id, ahora).Valor);
            }

            return new clsVerificacionIdentidad(
                new clsIdentidadVerificada(nuevo.Valor, rol, EsNuevo: true, disponiblesAlta), clsMotivosAcceso.AltaJit, ReglaDe(disponiblesAlta, regla));
        }

        if (porNombre.IdUsuarioExterno is { } vinculado && vinculado != externo.IdUsuarioExterno)
        {
            // El nombre ahora pertenece a otra fila del almacén.
            return Rechazo(clsMotivosAcceso.IdentidadInconsistente, regla, porNombre.Id);
        }

        if (permitirAlta)
        {
            // HU-040.3 (#258, ADR-009 OPCIÓN B+): en el LOGIN el rol interno se RECONCILIA con los roles externos vigentes, en vez de
            // rechazar ante cualquier desajuste. Se auto-OTORGAN los roles ORDINARIOS que falten (creando o reactivando la asignación) y se
            // auto-REVOCAN TODOS los que perdieron su respaldo externo (quitar privilegio es fail-safe). Los roles SENSIBLES
            // (requiereAltaManual, p. ej. director) que falten NO se auto-otorgan: se mantiene el rechazo + alta manual + auditoría. Solo se
            // reconcilia sobre la lectura EXITOSA del almacén: si el almacén falla ya se rechazó antes (almacen_no_disponible), así que nunca
            // se revoca por una lectura parcial (fail-closed).
            var asignaciones = await roles.ListarAsignacionesAsync(porNombre.Id, cancellationToken);
            var (rechazo, efectivos, otorgados, revocados) = Reconciliar(catalogo, asignaciones, mapeados, rol, porNombre.Id, ahora);
            if (rechazo is not null)
            {
                return Rechazo(rechazo, regla, porNombre.Id);
            }

            if (porNombre.IdUsuarioExterno is null)
            {
                porNombre.VincularUsuarioExterno(externo.IdUsuarioExterno);
            }

            // Login ya verificado: solo se refrescan datos que no deciden el acceso.
            var regional = await BuscarRegionalAsync(externo.NombreUsuario, cancellationToken);
            var correoAnterior = porNombre.Email;
            porNombre.ActualizarDatos(externo.NombreCompleto, externo.Email, regional);
            if (!string.Equals(correoAnterior, porNombre.Email, StringComparison.OrdinalIgnoreCase))
            {
                // El correo es el remitente y el contacto del comercial: se deja rastro del cambio, sin los valores.
                logger.LogWarning(
                    "El correo de '{NombreUsuario}' cambió en el almacén corporativo: sus cotizaciones nuevas salen con el correo nuevo.",
                    externo.NombreUsuario);
            }

            // HU-040.2 (#230): con un rol efectivo, sesión directa; con varios, elige al entrar.
            var disponiblesLogin = Disponibles(catalogo, efectivos);
            return new clsVerificacionIdentidad(
                new clsIdentidadVerificada(porNombre, rol, EsNuevo: false, disponiblesLogin, otorgados, revocados),
                clsMotivosAcceso.UsuarioExistente, ReglaDe(disponiblesLogin, regla));
        }

        // Refresh y elección de rol (permitirAlta: false): verificación ESTRICTA, NO reconcilia (ADR-009/ADR-025). Se compara contra el
        // CONJUNTO de asignaciones activas: el rol de la jerarquía debe estar asignado y ninguna activa puede haber perdido su rol externo.
        var asignados = await roles.ListarRolesAsignadosAsync(porNombre.Id, cancellationToken);
        if (asignados.Count == 0)
        {
            return Rechazo(clsMotivosAcceso.SinAsignacionActiva, regla, porNombre.Id);
        }

        if (!asignados.Contains(rol) || asignados.Any(a => !mapeados.Contains(a)))
        {
            return Rechazo(clsMotivosAcceso.RolExternoCambio, regla, porNombre.Id);
        }

        if (porNombre.IdUsuarioExterno is null)
        {
            porNombre.VincularUsuarioExterno(externo.IdUsuarioExterno);
        }

        var disponibles = asignados.Count == 1 ? Disponibles(catalogo, [rol]) : Disponibles(catalogo, asignados);
        return new clsVerificacionIdentidad(
            new clsIdentidadVerificada(porNombre, rol, EsNuevo: false, disponibles), clsMotivosAcceso.UsuarioExistente, ReglaDe(disponibles, regla));
    }

    /// <summary>
    /// HU-040.3 (#258, ADR-009 OPCIÓN B+): reconcilia las asignaciones del usuario con los roles externos vigentes (<paramref name="mapeados"/>)
    /// en el login. Devuelve el conjunto EFECTIVO de roles tras reconciliar y los deltas (para la auditoría), o un motivo de rechazo.
    /// <list type="bullet">
    /// <item>Auto-REVOCA (desactiva) toda asignación activa cuyo rol ya no está mapeado — todos los roles, incluidos los sensibles (fail-safe).</item>
    /// <item>Auto-OTORGA los roles ORDINARIOS mapeados que falten, reactivando la fila existente (sin duplicar, <c>uq_usuariosRoles_usuarioId_rolId</c>)
    /// o creando una nueva.</item>
    /// <item>Un rol SENSIBLE (<see cref="clsRol.RequiereAltaManual"/>) que falte NO se auto-otorga: si es el rol de la jerarquía, se RECHAZA
    /// (<c>rol_externo_cambio</c>) y queda para el alta manual, como antes.</item>
    /// </list>
    /// La comprobación del rol sensible se hace ANTES de mutar nada, así un rechazo no deja cambios a medias.
    /// </summary>
    private (string? Motivo, IReadOnlyList<RolInterno> Efectivos, IReadOnlyList<RolInterno> Otorgados, IReadOnlyList<RolInterno> Revocados)
        Reconciliar(
            IReadOnlyList<clsRol> catalogo, IReadOnlyList<clsUsuarioRol> asignaciones, IReadOnlyList<RolInterno> mapeados,
            RolInterno rol, Guid usuarioId, DateTimeOffset ahora)
    {
        var porId = catalogo.ToDictionary(r => r.Id);
        var porCodigo = catalogo.ToDictionary(r => r.Codigo);
        var sensibles = catalogo.Where(r => r.RequiereAltaManual).Select(r => r.Codigo).ToHashSet();
        var mapeadosSet = mapeados.ToHashSet();

        // Asignaciones activas con rol activo y conocido (la misma visibilidad que ListarRolesAsignadosAsync).
        var activas = asignaciones.Where(a => a.Activo && porId.ContainsKey(a.RolId)).ToList();
        var activos = activas.Select(a => porId[a.RolId].Codigo).ToHashSet();

        // El rol de la jerarquía (siempre ∈ mapeados) debe poder quedar efectivo. Si es SENSIBLE y no está asignado, no se auto-otorga:
        // rechazo + alta manual (sin tocar ninguna fila).
        if (sensibles.Contains(rol) && !activos.Contains(rol))
        {
            return (clsMotivosAcceso.RolExternoCambio, [], [], []);
        }

        var revocados = new List<RolInterno>();
        foreach (var activa in activas)
        {
            var codigo = porId[activa.RolId].Codigo;
            if (!mapeadosSet.Contains(codigo))
            {
                activa.Desactivar(ahora);
                revocados.Add(codigo);
            }
        }

        var otorgados = new List<RolInterno>();
        foreach (var codigo in mapeados)
        {
            if (activos.Contains(codigo) || sensibles.Contains(codigo))
            {
                continue;
            }

            var rolCatalogo = porCodigo[codigo];
            var existente = asignaciones.FirstOrDefault(a => a.RolId == rolCatalogo.Id);
            if (existente is not null)
            {
                existente.Reactivar(ahora);
            }
            else
            {
                roles.AgregarAsignacion(clsUsuarioRol.Asignar(usuarioId, rolCatalogo.Id, ahora).Valor);
            }

            otorgados.Add(codigo);
        }

        // Conjunto efectivo: las activas que siguen respaldadas + las recién otorgadas (en el orden de la jerarquía).
        var efectivos = activos.Where(mapeadosSet.Contains).Concat(otorgados).Distinct().Order().ToList();
        return (null, efectivos, otorgados, revocados);
    }

    /// <summary>
    /// Traduce los roles externos con el catálogo (<c>Roles.idRolExterno</c>, solo roles activos y mapeados). Sin ninguno:
    /// <c>null</c>. Con varios roles internos gana el de mayor alcance: <c>director</c> &gt; <c>gerente_comercial</c> &gt;
    /// <c>comercial</c> (HU-037, #210; reglas explícitas y auditadas, <c>precedencia_director</c> y <c>precedencia_gerente_comercial</c>;
    /// si luego pierde el rol que ganó, su asignación deja de coincidir y queda en revisión).
    /// </summary>
    public static (RolInterno? Rol, string? Regla) ResolverRol(IEnumerable<clsRol> catalogo, IEnumerable<clsRolExterno> rolesExternos)
    {
        var mapeados = RolesMapeados(catalogo, rolesExternos);

        return mapeados.Count switch
        {
            0 => (null, null),
            1 => (mapeados[0], clsMotivosAcceso.ReglaMapeoUnico),
            _ when mapeados.Contains(RolInterno.Director) => (RolInterno.Director, clsMotivosAcceso.ReglaPrecedenciaDirector),
            _ => (RolInterno.GerenteComercial, clsMotivosAcceso.ReglaPrecedenciaGerente),
        };
    }

    /// <summary>
    /// Roles internos a los que dan derecho los roles externos (<c>Roles.idRolExterno</c>, solo roles activos y mapeados), sin
    /// repetir y en el orden de la jerarquía (<c>comercial</c>, <c>gerente_comercial</c>, <c>director</c>).
    /// </summary>
    public static IReadOnlyList<RolInterno> RolesMapeados(IEnumerable<clsRol> catalogo, IEnumerable<clsRolExterno> rolesExternos)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        ArgumentNullException.ThrowIfNull(rolesExternos);

        var externos = rolesExternos.Select(r => r.IdRol).ToHashSet();
        return [.. catalogo
            .Where(r => r.Activo && r.IdRolExterno is { } id && externos.Contains(id))
            .Select(r => r.Codigo)
            .Distinct()
            .Order()];
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
        var esperada = false;
        try
        {
            // WaitAsync corta la espera aunque el cliente SQL tarde en atender la cancelación (p. ej. al abrir la conexión).
            consulta = directorio.ObtenerRegionalesAsync([nombreUsuario], tiempo.Token);
            var mapa = await consulta.WaitAsync(limite, cancellationToken);
            esperada = true;
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
            if (!esperada && consulta is not null)
            {
                // No se esperó con éxito (límite vencido, cancelación del llamador o fallo): se cancela y su error, llegue antes o
                // después, se observa aquí para que no quede como excepción sin observar.
                tiempo.Cancel();
                _ = consulta.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    /// <summary>Roles del catálogo con esos códigos, en el orden de la jerarquía (comercial, gerente_comercial, director).</summary>
    private static List<clsRol> Disponibles(IEnumerable<clsRol> catalogo, IEnumerable<RolInterno> codigos)
    {
        var buscados = codigos.ToHashSet();
        return [.. catalogo.Where(r => buscados.Contains(r.Codigo)).OrderBy(r => r.Codigo)];
    }

    /// <summary>HU-040.2 (#230): con varios roles disponibles la regla auditada es <see cref="clsMotivosAcceso.ReglaMultiRol"/>.</summary>
    private static string? ReglaDe(IReadOnlyCollection<clsRol> disponibles, string? regla) =>
        disponibles.Count > 1 ? clsMotivosAcceso.ReglaMultiRol : regla;

    private static clsVerificacionIdentidad Rechazo(string motivo, string? regla = null, Guid? usuarioId = null) => new(null, motivo, regla, usuarioId);
}

/// <summary>
/// Verificación doble de la identidad (login y refresh; hasta HU-013.9 también cada turno de Google Chat, retirado). Interfaz
/// para poder simularla en las pruebas.
/// </summary>
public interface IClsVerificadorIdentidad
{
    Task<clsVerificacionIdentidad> VerificarAsync(string idUsuario, bool permitirAlta, DateTimeOffset ahora, CancellationToken cancellationToken = default);
}
