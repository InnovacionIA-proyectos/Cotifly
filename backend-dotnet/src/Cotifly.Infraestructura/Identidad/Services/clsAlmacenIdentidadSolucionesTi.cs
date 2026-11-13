using System.Globalization;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Infraestructura.Identidad.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// Adaptador de <see cref="IClsAlmacenIdentidadExterno"/> sobre la base de identidad del portal (QA: <c>dbSolucionesTI_Prueba</c>, la misma de Nexo; <see cref="clsIdentidadDbContext"/>,
/// solo lectura). Los fallos de la base se propagan (el login responde 401, fail-closed); los datos que no se pueden
/// interpretar con seguridad se tratan como "no encontrado" con un warning.
/// </summary>
public sealed class clsAlmacenIdentidadSolucionesTi(clsIdentidadDbContext contexto, ILogger<clsAlmacenIdentidadSolucionesTi> logger)
    : IClsAlmacenIdentidadExterno
{
    /// <summary>Largo de <c>NOMBREUSUARIO</c> (VARCHAR(50)): un valor más largo no puede estar en la tabla.</summary>
    public const int LargoMaximoNombreUsuario = 50;

    public async Task<clsUsuarioExterno?> BuscarUsuarioAsync(string idUsuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idUsuario);
        var buscado = idUsuario.Trim();
        if (buscado.Length == 0)
        {
            return null;
        }

        if (buscado.Length <= LargoMaximoNombreUsuario)
        {
            var porNombre = await contexto.Usuarios.Where(u => u.NombreUsuario == buscado).Take(2).ToListAsync(cancellationToken);
            if (porNombre.Count > 1)
            {
                logger.LogWarning("NOMBREUSUARIO repetido en el almacén corporativo para '{IdUsuario}': se rechaza el login.", buscado);
                return null;
            }

            if (porNombre.Count == 1)
            {
                return Convertir(porNombre[0]);
            }
        }

        // Respaldo: el idUsuario como ID numérico (entero positivo, sin signo ni separadores).
        if (!long.TryParse(buscado, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) || numero <= 0)
        {
            return null;
        }

        decimal id = numero;
        var porId = await contexto.Usuarios.Where(u => u.Id == id).FirstOrDefaultAsync(cancellationToken);
        return porId is null ? null : Convertir(porId);
    }

    public async Task<IReadOnlyList<clsRolExterno>> ObtenerRolesAsync(long idUsuarioExterno, CancellationToken cancellationToken = default)
    {
        if (idUsuarioExterno is < int.MinValue or > int.MaxValue)
        {
            // tblSAPUsuarioRoles.IDUSUARIO es INT: un ID NUMERIC fuera de ese rango no puede tener roles.
            logger.LogWarning("El ID externo {IdUsuarioExterno} no cabe en tblSAPUsuarioRoles.IDUSUARIO (INT): sin roles.", idUsuarioExterno);
            return [];
        }

        var id = (int)idUsuarioExterno;
        var idsRoles = await contexto.UsuarioRoles.Where(r => r.IdUsuario == id).Select(r => r.IdRoles).Distinct().ToListAsync(cancellationToken);
        if (idsRoles.Count == 0)
        {
            return [];
        }

        var descripciones = await contexto.Roles.Where(r => idsRoles.Contains(r.IdRoles))
            .ToDictionaryAsync(r => r.IdRoles, r => r.Descripcion, cancellationToken);
        return [.. idsRoles.Order().Select(r => new clsRolExterno(r, descripciones.GetValueOrDefault(r)?.Trim()))];
    }

    private clsUsuarioExterno? Convertir(clsFilaUsuarioSap fila)
    {
        // ID es NUMERIC: solo se aceptan enteros positivos que quepan en BIGINT (UsuariosWeb.idUsuarioExterno).
        if (fila.Id != decimal.Truncate(fila.Id) || fila.Id <= 0 || fila.Id > long.MaxValue)
        {
            logger.LogWarning("ID {Id} del almacén corporativo no es un entero válido: se rechaza el login.", fila.Id);
            return null;
        }

        var nombre = fila.NombreUsuario?.Trim();
        if (string.IsNullOrEmpty(nombre))
        {
            logger.LogWarning("La fila {Id} del almacén corporativo no tiene NOMBREUSUARIO: se rechaza el login.", fila.Id);
            return null;
        }

        return new clsUsuarioExterno((long)fila.Id, nombre, fila.DescripcionUsuario?.Trim(), fila.Email?.Trim());
    }
}
