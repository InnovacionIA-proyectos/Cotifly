using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Emparejamiento de los camiones de Cotifly con las líneas de la solicitud de Nexo (HU-021.7, #134): N camiones por ruta, líneas en
/// cualquier orden, empates de camiones idénticos de una ruta resueltos en orden estable, la Mesa que le pone vehículo a una línea vacía
/// y la referencia de ruta de Nexo (N1) cuando exista.
/// </summary>
public sealed class clsEmparejadorCamionesTests
{
    [Fact]
    public void UnaLineaPorCamionLlevaLosDatosDeSuRutaYDeSuCamion()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), ("2S2", null, 8_000m), (null, null, 4_000m));

        var lineas = cotizacion.LineasNexo();

        lineas.Should().HaveCount(3, "Nexo cotiza cada camión como una línea");
        lineas.Select(l => l.Camion).Should().Equal(cotizacion.Vehiculos, "viajan en el orden estable de los camiones");
        lineas.Should().OnlyContain(l => l.Linea.Origen == "Buenaventura" && l.Linea.Destino == "Bogotá, D.C." && l.Linea.OrigenMunicipioId == 76109
                                         && l.Linea.Tipologia == "Contenedor" && l.Linea.ClaseViaje == "Contenedor" && l.Linea.HorasCargue == 6m
                                         && l.Linea.ValorMercancia == 90_000_000m && l.Linea.RutaId == ruta.Id && l.Linea.Posiciones.Count == 0);
        lineas.Select(l => (l.Linea.TipoVehiculo, l.Linea.Carroceria, l.Linea.PesoKg))
            .Should().Equal(lineas.Select(l => (l.Camion.TipoVehiculo, l.Camion.Carroceria, l.Camion.PesoKg)));
        lineas.Should().ContainSingle(l => l.Linea.TipoVehiculo == null && l.Linea.Carroceria == null,
            "el camión sin selección automática viaja igual, vacío, y lo resuelve la Mesa");
    }

    [Fact]
    public void NCamionesDeUnaRutaSeAsocianPorPesoYVehiculoEnCualquierOrden()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), ("2S2", null, 8_000m), (null, null, 4_000m));
        var camiones = Camiones(cotizacion);
        clsLineaSolicitudNexo[] nexo =
        [
            Linea("N-c", ruta, 4_000m, null, null),
            Linea("N-a", ruta, 10_000m, "3S3", "FURGON"),
            Linea("N-b", ruta, 8_000m, "2S2", null),
        ];

        foreach (var orden in Permutaciones(nexo))
        {
            var pares = clsEmparejadorLineasNexo.Emparejar(camiones, orden);

            pares.Should().HaveCount(3);
            pares[Camion(cotizacion, 10_000m).Id].Should().Be("N-a");
            pares[Camion(cotizacion, 8_000m).Id].Should().Be("N-b");
            pares[Camion(cotizacion, 4_000m).Id].Should().Be("N-c");
        }
    }

    [Fact]
    public void LosCamionesIdenticosDeUnaRutaSeAsignanEnOrdenEstable()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), ("3S3", "FURGON", 10_000m), ("2S2", null, 8_000m));
        var camiones = Camiones(cotizacion);
        clsLineaSolicitudNexo[] nexo = [Linea("N-z", ruta, 10_000m, "3S3", "FURGON"), Linea("N-2s2", ruta, 8_000m, "2S2", null), Linea("N-m", ruta, 10_000m, "3S3", "FURGON")];
        var identicos = camiones.Where(c => c.PesoKg == 10_000m).ToList();

        foreach (var orden in Permutaciones(nexo))
        {
            var pares = clsEmparejadorLineasNexo.Emparejar(camiones, orden);

            pares.Should().HaveCount(3, "dos camiones idénticos de la misma ruta son intercambiables: no se dejan sin asociar");
            pares[identicos[0].Id].Should().Be("N-m", "el primero en el orden de envío toma la primera línea por id de Nexo");
            pares[identicos[1].Id].Should().Be("N-z");
            pares[Camion(cotizacion, 8_000m).Id].Should().Be("N-2s2");
        }
    }

    [Fact]
    public void ElEmpateSeResuelveAunqueNexoNoTraigaElVehiculo()
    {
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m), ("3S3", null, 10_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones, [Linea("N2", ruta, 10_000m, null, null), Linea("N1", ruta, 10_000m, null, null)]);

        pares.Values.Should().BeEquivalentTo(new[] { "N1", "N2" });
        pares[camiones[0].Id].Should().Be("N1");
    }

    [Fact]
    public void SiLaMesaLePusoVehiculoALaLineaVaciaElCamionSeAsociaIgual()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), (null, null, 4_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones, [Linea("N-mesa", ruta, 4_000m, "2", "ESTACAS"), Linea("N-3s3", ruta, 10_000m, "3S3", "FURGON")]);

        pares[Camion(cotizacion, 4_000m).Id].Should().Be("N-mesa", "un dato que solo trae Nexo no contradice al camión vacío");
        pares[Camion(cotizacion, 10_000m).Id].Should().Be("N-3s3");
    }

    [Fact]
    public void UnEmpateQueOtroCamionPuedeReclamarNoSeAdivina()
    {
        // El camión vacío de 10 t y los dos 3S3 de 10 t pueden ser cualquiera de las tres líneas 3S3: no se adivina cuál es el vacío.
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m), ("3S3", null, 10_000m), (null, null, 10_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar(
            camiones, [Linea("N1", ruta, 10_000m, "3S3", null), Linea("N2", ruta, 10_000m, "3S3", null), Linea("N3", ruta, 10_000m, "3S3", null)]);

        pares.Should().BeEmpty();
    }

    [Fact]
    public void CamionesIgualesDeRutasDistintasNoSeAsignanPorOrden()
    {
        var cotizacion = Solicitud(2);
        var camiones = Camiones(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;

        clsEmparejadorLineasNexo.Emparejar(camiones, [Linea("N1", rutas[0], 10_000m, "3S3", null), Linea("N2", rutas[1], 10_000m, "3S3", null)])
            .Should().BeEmpty("dos rutas con el mismo contenido pueden costar distinto por datos que no viajan en la línea");
    }

    [Fact]
    public void ConLaReferenciaDeRutaDeNexoLasRutasIgualesSeDistinguen()
    {
        var cotizacion = Solicitud(2);
        var camiones = Camiones(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones,
        [
            Linea("N1", rutas[1], 10_000m, "3S3", null) with { ReferenciaRuta = rutas[1].Id.ToString("D") },
            Linea("N2", rutas[0], 10_000m, "3S3", null) with { ReferenciaRuta = rutas[0].Id.ToString("D").ToUpperInvariant() },
        ]);

        pares[rutas[0].Vehiculos[0].Id].Should().Be("N2", "N1 de Nexo: la referencia de la ruta decide");
        pares[rutas[1].Vehiculos[0].Id].Should().Be("N1");
    }

    [Fact]
    public void UnaReferenciaDeRutaDistintaNuncaCoincide()
    {
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m));
        var camion = Camiones(cotizacion)[0];

        clsEmparejadorLineasNexo.Coincide(camion, Linea("N1", ruta, 10_000m, "3S3", null) with { ReferenciaRuta = Guid.NewGuid().ToString() }).Should().BeFalse();
        clsEmparejadorLineasNexo.Coincide(camion, Linea("N1", ruta, 10_000m, "3S3", null) with { Ruta = null, ReferenciaRuta = ruta.Id.ToString() })
            .Should().BeTrue("con la referencia no hace falta el texto de la ruta");
    }

    [Fact]
    public void LasLineasYaAsignadasNoSeReasignan()
    {
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m), ("3S3", null, 10_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar([camiones[1]], [Linea("N1", ruta, 10_000m, null, null), Linea("N2", ruta, 10_000m, null, null)],
            new HashSet<string> { "N1" });

        pares.Should().HaveCount(1);
        pares[camiones[1].Id].Should().Be("N2", "N1 ya tiene dueño");
    }

    [Fact]
    public void UnaLineaHuerfanaDeOtraRutaIgualNoSeLaLlevaElCamionDeLaOtraRuta()
    {
        // Regresión (#134, validador): la ruta 1 tenía un 2S2 de 8 t que se descartó antes de asociarse, así que su línea quedó huérfana en
        // Nexo; la ruta 2 (mismo origen y destino) tiene un 2S2 de 8 t. Antes la ruta 2 se quedaba con la primera por id (la de la ruta 1).
        var cotizacion = Solicitud(2);
        var rutas = cotizacion.RutasOrdenadas;
        var deLaRuta2 = clsCamionParaEmparejar.Desde(rutas[1], rutas[1].Vehiculos[0]);
        clsLineaSolicitudNexo[] nexo = [Linea("A-huerfana-ruta1", rutas[0], 10_000m, "3S3", null), Linea("B-ruta2", rutas[1], 10_000m, "3S3", null)];

        var emparejamiento = clsEmparejadorLineasNexo.EmparejarConDetalle([deLaRuta2], nexo);

        emparejamiento.Pares.Should().BeEmpty("con una candidata de más no se sabe cuál es la suya: se avisa en vez de cruzar el costo");
        emparejamiento.EmpatesSinResolver.Should().ContainSingle().Which.Should().Contain("2 líneas candidatas");
        emparejamiento.LineasSinDueno.Should().BeEquivalentTo(new[] { "A-huerfana-ruta1", "B-ruta2" });
        emparejamiento.CamionesSinAsociar.Should().Equal(deLaRuta2.Id);

        // Con la referencia de ruta de Nexo (N1) sí se sabe.
        var conReferencia = clsEmparejadorLineasNexo.EmparejarConDetalle([deLaRuta2],
            [nexo[0] with { ReferenciaRuta = rutas[0].Id.ToString() }, nexo[1] with { ReferenciaRuta = rutas[1].Id.ToString() }]);
        conReferencia.Pares[deLaRuta2.Id].Should().Be("B-ruta2");
        conReferencia.LineasSinDueno.Should().Equal("A-huerfana-ruta1");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FuzzConLineasHuerfanasNuncaCruzaUnaLineaDeOtraRuta(int semilla)
    {
        // Rutas con el MISMO origen y destino, camiones de pocos tipos (muchos empates) y algunos descartados antes de asociarse, cuyas
        // líneas quedan huérfanas en Nexo. Cada línea sabe de qué ruta salió: ningún camión puede quedarse con una de otra ruta.
        var azar = new Random(semilla);
        var cruces = 0;
        var asociados = 0;
        for (var vuelta = 0; vuelta < 200; vuelta++)
        {
            var tipos = new (string? Vehiculo, decimal Peso)[] { ("3S3", 10_000m), ("2S2", 8_000m), (null, 8_000m) };
            var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-FUZZ" }).Valor;
            var numeroRutas = azar.Next(1, 4);
            cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
                [.. Enumerable.Range(0, numeroRutas).Select(_ =>
                {
                    var tipo = tipos[azar.Next(tipos.Length)];
                    return LineaCaptura((tipo.Vehiculo, null, tipo.Peso));
                })],
                [.. Enumerable.Range(0, numeroRutas).Select(i => (int?)i)], 3);
            foreach (var ruta in cotizacion.RutasOrdenadas)
            {
                for (var extra = azar.Next(0, 4); extra > 0; extra--)
                {
                    var tipo = tipos[azar.Next(tipos.Length)];
                    ruta.AgregarVehiculo(tipo.Vehiculo, null, tipo.Peso);
                }
            }

            var dueno = new Dictionary<string, Guid>();
            var lineas = new List<clsLineaSolicitudNexo>();
            var vigentes = new List<clsCamionParaEmparejar>();
            foreach (var ruta in cotizacion.RutasOrdenadas)
            {
                foreach (var camion in ruta.VehiculosOrdenados)
                {
                    var id = Guid.NewGuid().ToString();
                    dueno[id] = ruta.Id;
                    lineas.Add(Linea(id, ruta, camion.PesoKg!.Value, camion.TipoVehiculo, null));
                    if (azar.Next(4) != 0)
                    {
                        vigentes.Add(clsCamionParaEmparejar.Desde(ruta, camion)); // uno de cada cuatro se descarta: su línea queda huérfana
                    }
                }
            }

            var barajadas = lineas.OrderBy(_ => azar.Next()).ToList();
            var pares = clsEmparejadorLineasNexo.EmparejarConDetalle(vigentes, barajadas).Pares;
            asociados += pares.Count;
            cruces += pares.Count(p => dueno[p.Value] != vigentes.Single(c => c.Id == p.Key).RutaId);
        }

        cruces.Should().Be(0);
        asociados.Should().BeGreaterThan(0, "el fuzz también asocia");
    }

    // ------------------------------------------------------------------ Apoyo

    /// <summary>Ruta 1 (Buenaventura → Bogotá) con un camión por cada (vehículo, carrocería, peso), en ese orden de creación.</summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) Ruta(params (string? Vehiculo, string? Carroceria, decimal Peso)[] camiones)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CAM-1" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion }, [LineaCaptura(camiones[0])], [0], 3);
        var ruta = cotizacion.RutasOrdenadas[0];
        foreach (var camion in camiones.Skip(1))
        {
            ruta.AgregarVehiculo(camion.Vehiculo, camion.Carroceria, camion.Peso);
        }

        return (cotizacion, ruta);
    }

    /// <summary><paramref name="numero"/> rutas idénticas (Buenaventura → Bogotá), cada una con un 3S3 de 10 t.</summary>
    private static clsCotizacionSolicitud Solicitud(int numero)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CAM-2" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [.. Enumerable.Range(0, numero).Select(_ => LineaCaptura(("3S3", null, 10_000m)))], [.. Enumerable.Range(0, numero).Select(i => (int?)i)], 3);
        return cotizacion;
    }

    private static clsLineaSolicitudCosteo LineaCaptura((string? Vehiculo, string? Carroceria, decimal Peso) camion) => new()
    {
        Origen = "Buenaventura",
        Destino = "Bogotá, D.C.",
        OrigenMunicipioId = 76109,
        DestinoMunicipioId = 11001,
        Tipologia = "Contenedor",
        ClaseViaje = "Contenedor",
        ClaseContenedor = "CNT40.HC",
        HorasCargue = 6m,
        HorasDescargue = 5m,
        ValorMercancia = 90_000_000m,
        TipoVehiculo = camion.Vehiculo,
        Carroceria = camion.Carroceria,
        PesoKg = camion.Peso,
    };

    /// <summary>Los camiones de la solicitud como los ve el emparejador, en el orden en que viajan a Nexo.</summary>
    private static IReadOnlyList<clsCamionParaEmparejar> Camiones(clsCotizacionSolicitud cotizacion) =>
        [.. cotizacion.RutasOrdenadas.SelectMany(r => r.VehiculosOrdenados.Select(v => clsCamionParaEmparejar.Desde(r, v)))];

    private static clsVehiculoLineaSolicitud Camion(clsCotizacionSolicitud cotizacion, decimal peso) => cotizacion.Vehiculos.Single(v => v.PesoKg == peso);

    /// <summary>Línea de Nexo de la ruta como la normaliza Nexo (mayúsculas, sin tildes).</summary>
    private static clsLineaSolicitudNexo Linea(string id, clsLineaSolicitud ruta, decimal peso, string? vehiculo, string? carroceria) =>
        new(id, clsEmparejadorLineasNexo.Normalizar($"{ruta.OrigenNombre} → {ruta.DestinoNombre}"), "Contenedor", peso, vehiculo, carroceria);

    private static IEnumerable<IReadOnlyList<T>> Permutaciones<T>(IReadOnlyList<T> elementos)
    {
        if (elementos.Count <= 1)
        {
            yield return elementos;
            yield break;
        }

        for (var i = 0; i < elementos.Count; i++)
        {
            var indice = i;
            foreach (var resto in Permutaciones<T>([.. elementos.Where((_, j) => j != indice)]))
            {
                yield return [elementos[i], .. resto];
            }
        }
    }
}
