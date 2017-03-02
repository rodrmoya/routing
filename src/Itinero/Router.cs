﻿// Itinero - Routing for .NET
// Copyright (C) 2016 Abelshausen Ben
// 
// This file is part of Itinero.
// 
// Itinero is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 2 of the License, or
// (at your option) any later version.
// 
// Itinero is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Itinero. If not, see <http://www.gnu.org/licenses/>.

using Itinero.Algorithms.Default;
using Itinero.Algorithms.Routes;
using Itinero.Algorithms.Search;
using Itinero.Exceptions;
using Itinero.Graphs.Geometric;
using Itinero.Data.Network;
using Itinero.Profiles;
using System;
using System.Collections.Generic;
using Itinero.Data.Contracted;
using Itinero.Algorithms;
using Itinero.Algorithms.Weights;

namespace Itinero
{
    /// <summary>
    /// A router implementation encapsulating basic routing functionalities.
    /// </summary>
    public sealed class Router : RouterBase
    {
        private readonly RouterDb _db;

        /// <summary>
        /// Creates a new router.
        /// </summary>
        public Router(RouterDb db)
        {
            _db = db;

            this.ProfileFactorAndSpeedCache = new ProfileFactorAndSpeedCache(db);
            this.VerifyAllStoppable = false;
        }

        /// <summary>
        /// Gets or sets the delegate to create a custom resolver.
        /// </summary>
        public IResolveExtensions.CreateResolver CreateCustomResolver { get; set; }

        /// <summary>
        /// Gets or sets the custom route builder.
        /// </summary>
        public IRouteBuilder CustomRouteBuilder { get; set; }

        /// <summary>
        /// Gets the db.
        /// </summary>
        public override sealed RouterDb Db
        {
            get
            {
                return _db;
            }
        }

        /// <summary>
        /// Searches for the closest point on the routing network that's routable for the given profiles.
        /// </summary>
        /// <returns></returns>
        public sealed override Result<RouterPoint> TryResolve(IProfileInstance[] profileInstances, float latitude, float longitude,
            Func<RoutingEdge, bool> isBetter, float maxSearchDistance = Constants.SearchDistanceInMeter)
        {
            try
            {
                if (!_db.SupportsAll(profileInstances))
                {
                    return new Result<RouterPoint>("Not all routing profiles are supported.", (message) =>
                    {
                        return new Exceptions.ResolveFailedException(message);
                    });
                }

                IResolver resolver = null;

                // get is acceptable.
                var isAcceptable = this.GetIsAcceptable(profileInstances);

                if (this.CreateCustomResolver == null)
                { // just use the default resolver algorithm.
                    Func<GeometricEdge, bool> isBetterGeometric = null;
                    if (isBetter != null)
                    { // take into account isBetter function.
                        isBetterGeometric = (edge) =>
                            {
                                return isBetter(_db.Network.GetEdge(edge.Id));
                            };
                    }

                    // create resolver.
                    resolver = new ResolveAlgorithm(_db.Network.GeometricGraph, latitude, longitude,
                        _db.Network.MaxEdgeDistance / 2,
                            maxSearchDistance, isAcceptable, isBetterGeometric);
                }
                else
                { // create the custom resolver algorithm.
                    resolver = this.CreateCustomResolver(latitude, longitude, isAcceptable, isBetter);
                }
                resolver.Run();
                if (!resolver.HasSucceeded)
                { // something went wrong.
                    return new Result<RouterPoint>(resolver.ErrorMessage, (message) =>
                    {
                        return new Exceptions.ResolveFailedException(message);
                    });
                }
                return new Result<RouterPoint>(resolver.Result);
            }
            catch (Exception ex)
            {
                return new Result<RouterPoint>(ex.Message, (m) => ex);
            }
        }

        /// <summary>
        /// Checks if the given point is connected to the rest of the network. Use this to detect points on routing islands.
        /// </summary>
        /// <param name="radiusInMeter">The radius metric, that's always a distance in meters.</param>
        /// <returns></returns>
        public sealed override Result<bool> TryCheckConnectivity(IProfileInstance profileInstance, RouterPoint point, float radiusInMeter, bool? forward = null)
        {
            try
            {
                if (!_db.Supports(profileInstance.Profile))
                {
                    return new Result<bool>("Routing profile is not supported.", (message) =>
                    {
                        return new Exception(message);
                    });
                }

                // get the weight handler.
                var getGetFactor = this.GetDefaultGetFactor(profileInstance);
                Func<ushort, Factor> getShortestFactor = (p) =>
                { // only keep directional information, get factor to 1 for distance metrics only.
                    var factor = getGetFactor(p);
                    if (factor.Value == 0)
                    {
                        return new Factor()
                        {
                            Direction = factor.Direction,
                            Value = 0
                        };
                    }
                    return new Factor()
                    { 
                        Direction = factor.Direction,
                        Value = 1
                    };
                };
                var weightHandler = new DefaultWeightHandler(getShortestFactor);

                var checkForward = forward == null || forward.Value;
                var checkBackward = forward == null || !forward.Value;

                if (checkForward)
                { // build and run forward dykstra search.
                    var dykstra = new Dykstra(_db.Network.GeometricGraph.Graph, weightHandler, null,
                        point.ToEdgePaths(_db, weightHandler, true), radiusInMeter, false);
                    dykstra.Run();
                    if (!dykstra.HasSucceeded ||
                        !dykstra.MaxReached)
                    { // something went wrong or max not reached.
                        return new Result<bool>(false);
                    }
                }

                
                if (checkBackward)
                { // build and run backward dykstra search.
                    var dykstra = new Dykstra(_db.Network.GeometricGraph.Graph, weightHandler, null,
                        point.ToEdgePaths(_db, weightHandler, false), radiusInMeter, true);
                    dykstra.Run();
                    if (!dykstra.HasSucceeded ||
                        !dykstra.MaxReached)
                    { // something went wrong or max not reached.
                        return new Result<bool>(false);
                    }
                }
                return new Result<bool>(true);
            }
            catch (Exception ex)
            {
                return new Result<bool>(ex.Message, (m) => ex);
            }
        }

        /// <summary>
        /// Calculates a route between the two locations.
        /// </summary>
        /// <returns></returns>
        public sealed override Result<EdgePath<T>> TryCalculateRaw<T>(IProfileInstance profileInstance, WeightHandler<T> weightHandler, RouterPoint source, RouterPoint target,
            RoutingSettings<T> settings)
        {
            try
            {
                if (!_db.Supports(profileInstance.Profile))
                {
                    return new Result<EdgePath<T>>("Routing profile is not supported.", (message) =>
                    {
                        return new Exception(message);
                    });
                }

                var maxSearch = weightHandler.Infinite;
                if (settings != null)
                {
                    if (!settings.TryGetMaxSearch(profileInstance.Profile.FullName, out maxSearch))
                    {
                        maxSearch = weightHandler.Infinite;
                    }
                }

                ContractedDb contracted;

                bool useContracted = false;
                if (_db.TryGetContracted(profileInstance.Profile, out contracted))
                { // contracted calculation.
                    useContracted = true;
                    if (_db.HasComplexRestrictions(profileInstance.Profile) && !contracted.HasEdgeBasedGraph)
                    { // there is no edge-based graph for this profile but the db has complex restrictions, don't use the contracted graph.
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a vertex-based contracted graph but also complex restrictions. Not using the contracted graph, add an edge-based contracted graph.");
                        useContracted = false;
                    }
                }

                EdgePath<T> path = null;
                if (source.EdgeId == target.EdgeId)
                { // check for a path on the same edge.
                    var edgePath = source.EdgePathTo(_db, weightHandler, target);
                    if (edgePath != null)
                    {
                        path = edgePath;
                    }
                }

                if (useContracted)
                {  // use the contracted graph.
                    List<uint> vertexPath = null;

                    if (!contracted.HasEdgeBasedGraph)
                    { // use node-based routing.
                        var bidirectionalSearch = new Itinero.Algorithms.Contracted.BidirectionalDykstra<T>(contracted.NodeBasedGraph, weightHandler,
                            source.ToEdgePaths(_db, weightHandler, true), target.ToEdgePaths(_db, weightHandler, false));
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            if (path == null)
                            {
                                return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                                {
                                    return new RouteNotFoundException(message);
                                });
                            }
                        }
                        else
                        {
                            vertexPath = bidirectionalSearch.GetPath();
                        }
                    }
                    else
                    { // use edge-based routing.
                        var bidirectionalSearch = new Itinero.Algorithms.Contracted.EdgeBased.BidirectionalDykstra<T>(contracted.EdgeBasedGraph, weightHandler,
                            source.ToEdgePaths(_db, weightHandler, true), target.ToEdgePaths(_db, weightHandler, false), _db.GetGetRestrictions(profileInstance.Profile, null));
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            if (path == null)
                            {
                                return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                                {
                                    return new RouteNotFoundException(message);
                                });
                            }
                        }
                        else
                        {
                            vertexPath = bidirectionalSearch.GetPath();
                        }
                    }

                    // expand vertex path using the regular graph.
                    if (vertexPath != null)
                    {
                        var localPath = _db.BuildEdgePath(weightHandler, source, target, vertexPath);
                        if (path == null || 
                            weightHandler.IsSmallerThan(localPath.Weight, path.Weight))
                        {
                            path = localPath;
                        }
                    }
                }
                else
                { // use the regular graph.
                    EdgePath<T> localPath = null;

                    if (_db.HasComplexRestrictions(profileInstance.Profile))
                    {
                        var sourceSearch = new Algorithms.Default.EdgeBased.Dykstra<T>(_db.Network.GeometricGraph.Graph, weightHandler,
                            _db.GetGetRestrictions(profileInstance.Profile, true), source.ToEdgePaths(_db, weightHandler, true), maxSearch, false);
                        var targetSearch = new Algorithms.Default.EdgeBased.Dykstra<T>(_db.Network.GeometricGraph.Graph, weightHandler,
                            _db.GetGetRestrictions(profileInstance.Profile, false), target.ToEdgePaths(_db, weightHandler, false), maxSearch, true);

                        var bidirectionalSearch = new Algorithms.Default.EdgeBased.BidirectionalDykstra<T>(sourceSearch, targetSearch, weightHandler);
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            if (path == null)
                            {
                                return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                                {
                                    return new RouteNotFoundException(message);
                                });
                            }
                        }
                        else
                        {
                            localPath = bidirectionalSearch.GetPath();
                        }
                    }
                    else
                    {
                        var sourceSearch = new Dykstra<T>(_db.Network.GeometricGraph.Graph, null, weightHandler,
                            source.ToEdgePaths(_db, weightHandler, true), maxSearch, false);
                        var targetSearch = new Dykstra<T>(_db.Network.GeometricGraph.Graph, null, weightHandler,
                            target.ToEdgePaths(_db, weightHandler, false), maxSearch, true);

                        var bidirectionalSearch = new BidirectionalDykstra<T>(sourceSearch, targetSearch, weightHandler);
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            if (path == null)
                            {
                                return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                                {
                                    return new RouteNotFoundException(message);
                                });
                            }
                        }
                        else
                        {
                            localPath = bidirectionalSearch.GetPath();
                        }
                    }

                    // choose best path.
                    if (localPath != null)
                    {
                        if (path == null ||
                            weightHandler.IsSmallerThan(localPath.Weight, path.Weight))
                        {
                            path = localPath;
                        }
                    }
                }
                return new Result<EdgePath<T>>(path);
            }
            catch(Exception ex)
            {
                return new Result<EdgePath<T>>(ex.Message, (m) => ex);
            }
        }

        /// <summary>
        /// Calculates a route between the two directed edges. The route starts in the direction of the edge and ends with an arrive in the direction of the target edge.
        /// </summary>
        /// <returns></returns>
        public sealed override Result<EdgePath<T>> TryCalculateRaw<T>(IProfileInstance profileInstance, WeightHandler<T> weightHandler, long sourceDirectedEdge, long targetDirectedEdge,
            RoutingSettings<T> settings)
        {
            try
            {
                if (!_db.Supports(profileInstance.Profile))
                {
                    return new Result<EdgePath<T>>("Routing profile is not supported.", (message) =>
                    {
                        return new Exception(message);
                    });
                }

                var maxSearch = weightHandler.Infinite;
                if (settings != null)
                {
                    if (!settings.TryGetMaxSearch(profileInstance.Profile.FullName, out maxSearch))
                    {
                        maxSearch = weightHandler.Infinite;
                    }
                }

                var sourcePath = _db.GetPathForEdge(weightHandler, sourceDirectedEdge, true);
                var targetPath = _db.GetPathForEdge(weightHandler, targetDirectedEdge, false);

                if (sourceDirectedEdge == targetDirectedEdge)
                { // when edges match, path is always the edge itself.
                    var edgePath = sourcePath;
                    if (edgePath != null)
                    {
                        return new Result<EdgePath<T>>(edgePath);
                    }
                }

                EdgePath<T> path;
                ContractedDb contracted;

                bool useContracted = false;
                if (_db.TryGetContracted(profileInstance.Profile, out contracted))
                { // contracted calculation.
                    useContracted = true;
                    if (_db.HasComplexRestrictions(profileInstance.Profile) && !contracted.HasEdgeBasedGraph)
                    { // there is no edge-based graph for this profile but the db has complex restrictions, don't use the contracted graph.
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a vertex-based contracted graph but also complex restrictions. Not using the contracted graph, add an edge-based contracted graph.");
                        useContracted = false;
                    }
                    if (!contracted.HasEdgeBasedGraph)
                    {
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a vertex-based contracted graph but it cannot be used to calculate routes with a start and end edge in a specific direction.");
                        useContracted = false;
                    }
                }

                if (useContracted)
                {  // use the contracted graph.
                    path = null;

                    List<uint> vertexPath = null;

                    if (!contracted.HasEdgeBasedGraph)
                    { // use node-based routing.
                        throw new Exception("Cannot use vertex-based contracted graph for edge-based calculations.");
                    }
                    else
                    { // use edge-based routing.
                        var bidirectionalSearch = new Algorithms.Contracted.EdgeBased.BidirectionalDykstra<T>(contracted.EdgeBasedGraph, weightHandler,
                            new EdgePath<T>[] { sourcePath }, new EdgePath<T>[] { targetPath }, _db.GetGetRestrictions(profileInstance.Profile, null));
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        vertexPath = bidirectionalSearch.GetPath();
                    }

                    // expand vertex path using the regular graph.
                    var source = _db.CreateRouterPointForEdge(sourceDirectedEdge, true);
                    var target = _db.CreateRouterPointForEdge(targetDirectedEdge, false);
                    path = _db.BuildEdgePath(weightHandler, source, target, vertexPath);
                }
                else
                { // use the regular graph.
                    if (_db.HasComplexRestrictions(profileInstance.Profile))
                    {
                        var sourceSearch = new Algorithms.Default.EdgeBased.Dykstra<T>(_db.Network.GeometricGraph.Graph, weightHandler,
                            _db.GetGetRestrictions(profileInstance.Profile, true), new EdgePath<T>[] { sourcePath }, maxSearch, false);
                        var targetSearch = new Algorithms.Default.EdgeBased.Dykstra<T>(_db.Network.GeometricGraph.Graph, weightHandler,
                            _db.GetGetRestrictions(profileInstance.Profile, false), new EdgePath<T>[] { targetPath }, maxSearch, true);

                        var bidirectionalSearch = new Algorithms.Default.EdgeBased.BidirectionalDykstra<T>(sourceSearch, targetSearch, weightHandler);
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        path = bidirectionalSearch.GetPath();
                    }
                    else
                    {
                        var sourceSearch = new Dykstra<T>(_db.Network.GeometricGraph.Graph, null, weightHandler,
                            new EdgePath<T>[] { sourcePath }, maxSearch, false);
                        var targetSearch = new Dykstra<T>(_db.Network.GeometricGraph.Graph, null, weightHandler,
                            new EdgePath<T>[] { targetPath }, maxSearch, true);

                        var bidirectionalSearch = new BidirectionalDykstra<T>(sourceSearch, targetSearch, weightHandler);
                        bidirectionalSearch.Run();
                        if (!bidirectionalSearch.HasSucceeded)
                        {
                            return new Result<EdgePath<T>>(bidirectionalSearch.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        path = bidirectionalSearch.GetPath();
                    }
                }

                return new Result<EdgePath<T>>(path);
            }
            catch (Exception ex)
            {
                return new Result<EdgePath<T>>(ex.Message, (m) => ex);
            }
        }

        /// <summary>
        /// Calculates all routes between all sources and all targets.
        /// </summary>
        /// <returns></returns>
        public sealed override Result<EdgePath<T>[][]> TryCalculateRaw<T>(IProfileInstance profileInstance, WeightHandler<T> weightHandler, RouterPoint[] sources, RouterPoint[] targets,
            RoutingSettings<T> settings)
        {
            try
            {
                if (!_db.Supports(profileInstance.Profile))
                {
                    return new Result<EdgePath<T>[][]>("Routing profile is not supported.", (message) =>
                    {
                        return new Exception(message);
                    });
                }

                var maxSearch = weightHandler.Infinite;
                if (settings != null)
                {
                    if (!settings.TryGetMaxSearch(profileInstance.Profile.FullName, out maxSearch))
                    {
                        maxSearch = weightHandler.Infinite;
                    }
                }

                ContractedDb contracted;
                EdgePath<T>[][] paths = null;

                bool useContracted = false;
                if (_db.TryGetContracted(profileInstance.Profile, out contracted))
                { // contracted calculation.
                    useContracted = true;
                    if (_db.HasComplexRestrictions(profileInstance.Profile) && !contracted.HasEdgeBasedGraph)
                    { // there is no edge-based graph for this profile but the db has complex restrictions, don't use the contracted graph.
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a vertex-based contracted graph but also complex restrictions. Not using the contracted graph, add an edge-based contracted graph.");
                        useContracted = false;
                    }

                    if (!weightHandler.CanUse(contracted))
                    { // there is a contracted graph but it is not equipped to handle this weight-type.
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a contracted graph but it's not built for the given weight calculations, using the default but slow implementation.");
                        useContracted = false;
                    }
                }

                if (useContracted)
                {
                    if (!contracted.HasEdgeBasedGraph)
                    { // use node-based routing.
                        var algorithm = new Itinero.Algorithms.Contracted.ManyToManyBidirectionalDykstra<T>(_db, profileInstance.Profile, weightHandler,
                            sources, targets, maxSearch);
                        algorithm.Run();
                        if (!algorithm.HasSucceeded)
                        {
                            return new Result<EdgePath<T>[][]>(algorithm.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }

                        // build all routes.
                        paths = new EdgePath<T>[sources.Length][];
                        for (var s = 0; s < sources.Length; s++)
                        {
                            paths[s] = new EdgePath<T>[targets.Length];
                            for (var t = 0; t < targets.Length; t++)
                            {
                                paths[s][t] = algorithm.GetPath(s, t);
                            }
                        }
                    }
                    else
                    { // use edge-based routing.
                        var algorithm = new Itinero.Algorithms.Contracted.EdgeBased.ManyToManyBidirectionalDykstra<T>(_db, profileInstance.Profile, weightHandler,
                            sources, targets, maxSearch);
                        algorithm.Run();
                        if (!algorithm.HasSucceeded)
                        {
                            return new Result<EdgePath<T>[][]>(algorithm.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }

                        // build all routes.
                        paths = new EdgePath<T>[sources.Length][];
                        for (var s = 0; s < sources.Length; s++)
                        {
                            paths[s] = new EdgePath<T>[targets.Length];
                            for (var t = 0; t < targets.Length; t++)
                            {
                                paths[s][t] = algorithm.GetPath(s, t);
                            }
                        }
                    }
                }

                if (paths == null)
                {
                    // use non-contracted calculation.
                    var algorithm = new Itinero.Algorithms.Default.ManyToMany<T>(_db, weightHandler, sources, targets, maxSearch);
                    algorithm.Run();
                    if (!algorithm.HasSucceeded)
                    {
                        return new Result<EdgePath<T>[][]>(algorithm.ErrorMessage, (message) =>
                        {
                            return new RouteNotFoundException(message);
                        });
                    }

                    // build all routes.
                    paths = new EdgePath<T>[sources.Length][];
                    for (var s = 0; s < sources.Length; s++)
                    {
                        paths[s] = new EdgePath<T>[targets.Length];
                        for (var t = 0; t < targets.Length; t++)
                        {
                            paths[s][t] = algorithm.GetPath(s, t);
                        }
                    }
                }
                return new Result<EdgePath<T>[][]>(paths);
            }
            catch (Exception ex)
            {
                return new Result<EdgePath<T>[][]>(ex.Message, (m) => ex);
            }
        }

        /// <summary>
        /// Calculates all routes between all sources and all targets.
        /// </summary>
        /// <returns></returns>
        public sealed override Result<T[][]> TryCalculateWeight<T>(IProfileInstance profileInstance, WeightHandler<T> weightHandler, RouterPoint[] sources, RouterPoint[] targets,
            ISet<int> invalidSources, ISet<int> invalidTargets, RoutingSettings<T> settings)
        {
            try
            {
                if (!_db.Supports(profileInstance.Profile))
                {
                    return new Result<T[][]>("Routing profile is not supported.", (message) =>
                    {
                        return new Exception(message);
                    });
                }

                var maxSearch = weightHandler.Infinite;
                if (settings != null)
                {
                    if (!settings.TryGetMaxSearch(profileInstance.Profile.FullName, out maxSearch))
                    {
                        maxSearch = weightHandler.Infinite;
                    }
                }

                ContractedDb contracted;
                T[][] weights = null;

                bool useContracted = false;
                if (_db.TryGetContracted(profileInstance.Profile, out contracted))
                { // contracted calculation.
                    useContracted = true;
                    if (_db.HasComplexRestrictions(profileInstance.Profile) && !contracted.HasEdgeBasedGraph)
                    { // there is no edge-based graph for this profile but the db has complex restrictions, don't use the contracted graph.
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a vertex-based contracted graph but also complex restrictions. Not using the contracted graph, add an edge-based contracted graph.");
                        useContracted = false;
                    }

                    if (!weightHandler.CanUse(contracted))
                    { // there is a contracted graph but it is not equipped to handle this weight-type.
                        Logging.Logger.Log("Router", Logging.TraceEventType.Warning,
                            "There is a contracted graph but it's not built for the given weight calculations, using the default but slow implementation.");
                        useContracted = false;
                    }
                }

                if (useContracted)
                {
                    if (!contracted.HasEdgeBasedGraph)
                    { // use node-based routing.
                        var algorithm = new Itinero.Algorithms.Contracted.ManyToManyWeightsBidirectionalDykstra<T>(_db, profileInstance.Profile, weightHandler,
                            sources, targets, maxSearch);
                        algorithm.Run();
                        if (!algorithm.HasSucceeded)
                        {
                            return new Result<T[][]>(algorithm.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        weights = algorithm.Weights;
                    }
                    else
                    { // use edge-based routing.
                        var algorithm = new Itinero.Algorithms.Contracted.EdgeBased.ManyToManyWeightsBidirectionalDykstra<T>(_db, profileInstance.Profile, weightHandler,
                            sources, targets, maxSearch);
                        algorithm.Run();
                        if (!algorithm.HasSucceeded)
                        {
                            return new Result<T[][]>(algorithm.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        weights = algorithm.Weights;
                    }
                }
                else
                { // use regular graph.
                    if (_db.HasComplexRestrictions(profileInstance.Profile))
                    {
                        var algorithm = new Itinero.Algorithms.Default.EdgeBased.ManyToMany<T>(this, weightHandler, _db.GetGetRestrictions(profileInstance.Profile, true), sources, targets, maxSearch);
                        algorithm.Run();
                        if (!algorithm.HasSucceeded)
                        {
                            return new Result<T[][]>(algorithm.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        weights = algorithm.Weights;
                    }
                    else
                    {
                        var algorithm = new Itinero.Algorithms.Default.ManyToMany<T>(_db, weightHandler, sources, targets, maxSearch);
                        algorithm.Run();
                        if (!algorithm.HasSucceeded)
                        {
                            return new Result<T[][]>(algorithm.ErrorMessage, (message) =>
                            {
                                return new RouteNotFoundException(message);
                            });
                        }
                        weights = algorithm.Weights;
                    }
                }

                // check for invalids.
                var invalidTargetCounts = new int[targets.Length];
                for (var s = 0; s < weights.Length; s++)
                {
                    var invalids = 0;
                    for (var t = 0; t < weights[s].Length; t++)
                    {
                        if (t != s)
                        {
                            if (weightHandler.GetMetric(weights[s][t]) == float.MaxValue)
                            {
                                invalids++;
                                invalidTargetCounts[t]++;
                                if (invalidTargetCounts[t] > (sources.Length - 1) / 2)
                                {
                                    invalidTargets.Add(t);
                                }
                            }
                        }
                    }

                    if (invalids > (targets.Length - 1) / 2)
                    {
                        invalidSources.Add(s);
                    }
                }
                return new Result<T[][]>(weights);
            }
            catch (Exception ex)
            {
                return new Result<T[][]>(ex.Message, (m) => ex);
            }
        }

        /// <summary>
        /// Builds a route.
        /// </summary>
        public override sealed Result<Route> BuildRoute<T>(IProfileInstance profileInstance, WeightHandler<T> weightHandler, RouterPoint source, RouterPoint target, EdgePath<T> path)
        {
            try
            {
                if (this.CustomRouteBuilder != null)
                { // there is a custom route builder.
                    return this.CustomRouteBuilder.TryBuild(_db, profileInstance.Profile, source, target, path);
                }

                // use the default.
                return CompleteRouteBuilder.TryBuild(_db, profileInstance.Profile, source, target, path);
            }
            catch (Exception ex)
            {
                return new Result<Route>(ex.Message, (m) => ex);
            }
        }
    }
}