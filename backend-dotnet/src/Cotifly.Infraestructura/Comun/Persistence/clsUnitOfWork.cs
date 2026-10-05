using System.Data;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Unidad de trabajo sobre <see cref="clsCotiflyDbContext"/>. Traduce las violaciones de restricciones
/// únicas (SQL Server 2627/2601) a <see cref="clsViolacionUnicidadException"/> con un código de negocio.
/// </summary>
public sealed partial class clsUnitOfWork(clsCotiflyDbContext contexto) : IClsUnitOfWork
{
    /// <summary>Violation of UNIQUE KEY / PRIMARY KEY constraint.</summary>
    private const int ErrorRestriccionUnica = 2627;

    /// <summary>Cannot insert duplicate key row ... with unique index.</summary>
    private const int ErrorIndiceUnico = 2601;

    private static readonly Dictionary<string, clsErrorDominio> _erroresPorRestriccion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uq_cotizaciones_folio"] = new(clsViolacionUnicidadException.CodigoFolioDuplicado, "Ya existe una cotización con ese folio."),
        ["uq_usuariosWeb_nombreUsuario"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "Ya existe un usuario con ese nombre de usuario."),
        ["uq_margenesPolitica_lineaNegocio"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "Ya existe esa línea de negocio."),
        ["uq_usuariosWeb_idUsuarioExterno"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "Ya existe un usuario vinculado a esa fila del almacén corporativo."),
        ["uq_roles_idRolExterno"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "Ese rol externo ya está mapeado a otro rol interno."),
        ["uq_usuariosRoles_usuarioId_activo"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "El usuario ya tiene un rol activo asignado."),
        ["uq_usuariosRoles_usuarioId_rolId"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "El usuario ya tiene ese rol asignado."),
        ["pk_tokensRevocados"] = new(clsViolacionUnicidadException.CodigoRegistroDuplicado, "Ese access token ya estaba revocado."),
    };

    public async Task<int> GuardarCambiosAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await contexto.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: ErrorRestriccionUnica or ErrorIndiceUnico } sql)
        {
            contexto.ChangeTracker.Clear();
            var restriccion = ExtraerRestriccion(sql.Message);
            var error = restriccion is not null && _erroresPorRestriccion.TryGetValue(restriccion, out var conocido)
                ? conocido
                : new clsErrorDominio(clsViolacionUnicidadException.CodigoRegistroDuplicado, "El registro ya existe.", restriccion);
            throw new clsViolacionUnicidadException(restriccion, error, ex);
        }
    }

    public async Task<bool> GuardarCambiosSiVersionAsync(Guid cotizacionId, byte[] versionEsperada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionEsperada);
        if (contexto.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Ya hay una transacción abierta en esta unidad de trabajo: el control de versión no se anida.");
        }

        await using var transaccion = await contexto.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // UPDLOCK retiene la fila hasta confirmar: entre la comparación y el UPDATE nadie más (polling, otra petición) la escribe.
        var actual = await contexto.Database
            .SqlQuery<byte[]>($"SELECT rowVersion AS [Value] FROM dbo_cotifly.Cotizaciones WITH (UPDLOCK, ROWLOCK) WHERE id = {cotizacionId}")
            .FirstOrDefaultAsync(cancellationToken);
        if (actual is null || !actual.AsSpan().SequenceEqual(versionEsperada))
        {
            await transaccion.RollbackAsync(CancellationToken.None);
            contexto.ChangeTracker.Clear();
            return false;
        }

        await GuardarCambiosAsync(cancellationToken);
        await transaccion.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Nombre de la restricción en el mensaje de SQL Server (entre comillas simples; en cualquier idioma
    /// el nombre aparece citado). Se toma el primer identificador con prefijo de restricción del esquema.
    /// </summary>
    internal static string? ExtraerRestriccion(string mensaje) =>
        ExpresionCitado().Matches(mensaje)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(n => n.StartsWith("uq_", StringComparison.OrdinalIgnoreCase)
                                 || n.StartsWith("pk_", StringComparison.OrdinalIgnoreCase)
                                 || n.StartsWith("ix_", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionCitado();
}
